using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Infrastructure;
using Quesshi.Infrastructure.Generation;
using Quesshi.Infrastructure.Localisation;
using Quesshi.Infrastructure.Media;
using Quesshi.Infrastructure.Security;
using Quesshi.Infrastructure.Mongo;
using Quesshi.Infrastructure.Otp;
using Quesshi.Infrastructure.Redis;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Seed;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
var mongoOptions = new MongoOptions
{
    ConnectionString = builder.Configuration.GetConnectionString("Mongo") ?? "mongodb://localhost:27017",
    Database = builder.Configuration["Mongo:Database"] ?? "quesshi"
};

// --- Orleans -------------------------------------------------------------------------
// Redis carries clustering, hot grain state and reminders. Mongo carries everything durable,
// through the repositories rather than through an Orleans provider.
builder.UseOrleans(silo =>
{
    silo.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = builder.Configuration["Orleans:ClusterId"] ?? "quesshi";
        options.ServiceId = "quesshi";
    });

    silo.UseRedisClustering(options => options.ConfigurationOptions = ConfigurationOptions.Parse(redisConnection));
    silo.AddRedisGrainStorage("hot", options => options.ConfigurationOptions = ConfigurationOptions.Parse(redisConnection));
    silo.UseRedisReminderService(options => options.ConfigurationOptions = ConfigurationOptions.Parse(redisConnection));

    // A startup task runs once the silo is actually up; a plain Task.Run races it and throws.
    var nightly = builder.Configuration.GetValue("Generation:Nightly", false);
    silo.AddStartupTask(async (services, ct) =>
        await services.GetRequiredService<IGrainFactory>().GetGrain<IQuestionGeneratorGrain>(0).ApplyScheduleAsync(nightly));
});

// --- configuration objects -----------------------------------------------------------
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var adminAuthOptions = builder.Configuration.GetSection("AdminAuth").Get<AdminAuthOptions>() ?? new AdminAuthOptions();
var smtpOptions = builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
var openRouterOptions = builder.Configuration.GetSection("OpenRouter").Get<OpenRouterOptions>() ?? new OpenRouterOptions();
var topUpOptions = builder.Configuration.GetSection("Generation").Get<TopUpOptions>() ?? new TopUpOptions();

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton(adminAuthOptions);
builder.Services.AddSingleton(smtpOptions);
builder.Services.AddSingleton(openRouterOptions);
builder.Services.AddSingleton(topUpOptions);
builder.Services.AddSingleton(mongoOptions);

// --- infrastructure ------------------------------------------------------------------
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ITranslator>(sp => new JsonFileTranslator(
    Path.Combine(builder.Environment.ContentRootPath, "i18n"),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonFileTranslator>()));
builder.Services.AddSingleton<IIdFactory, IdFactory>();
builder.Services.AddSingleton<IQuestionRepository, MongoQuestionRepository>();
builder.Services.AddSingleton<ICategoryRepository, MongoCategoryRepository>();
builder.Services.AddSingleton<IPlayerRepository, MongoPlayerRepository>();
builder.Services.AddSingleton<IAdminUserRepository, MongoAdminUserRepository>();
builder.Services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
builder.Services.AddSingleton<IResetTokenStore, RedisResetTokenStore>();
builder.Services.AddSingleton<IMatchArchive, MongoMatchArchive>();
builder.Services.AddSingleton<IGenerationLog, MongoGenerationLog>();
builder.Services.AddSingleton<IAiSpendLog, MongoAiSpendLog>();
builder.Services.AddSingleton<ILeaderboard, RedisLeaderboard>();
builder.Services.AddSingleton<IOtpStore, RedisOtpStore>();
builder.Services.AddSingleton<QuestionPromptBuilder>();
builder.Services.AddSingleton<IQuestionGenerator, OpenRouterQuestionGenerator>();

var imageOptions = builder.Configuration.GetSection("Images").Get<WikipediaImageOptions>() ?? new WikipediaImageOptions();
// Pictures are written under the web root so they are served like any other static file.
imageOptions.StorageRoot = Path.Combine(builder.Environment.WebRootPath ?? "wwwroot", "media", "sourced");
builder.Services.AddSingleton(imageOptions);
builder.Services.AddSingleton<IQuestionImageProvider, WikipediaImageProvider>();

// Codes and reset links are mailed, everywhere, with no exception for development: a local catcher
// such as Mailpit gives you the mail without a mailbox, which is what the echo used to be for.
builder.Services.AddSingleton<IOtpSender, SmtpOtpSender>();
builder.Services.AddSingleton<IAdminMailer, SmtpAdminMailer>();

// --- application ---------------------------------------------------------------------
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<QuestionSetBuilder>();
builder.Services.AddSingleton<TopUpQuestionBank>();
builder.Services.AddSingleton<Seeder>();
builder.Services.AddSingleton<TokenIssuer>();

// --- auth ----------------------------------------------------------------------------
var tokenIssuer = new TokenIssuer(jwtOptions);
var adminTokenIssuer = new AdminTokenIssuer(adminAuthOptions);
builder.Services.AddSingleton(tokenIssuer);
builder.Services.AddSingleton(adminTokenIssuer);

// Two schemes, two audiences, two signing keys. A player token cannot be presented as an admin
// token even if something else goes wrong, because it will not validate against the admin scheme.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwtOptions.Issuer,
        ValidAudience = jwtOptions.Audience,
        IssuerSigningKey = tokenIssuer.SigningKey,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    })
    .AddJwtBearer(AdminTokenIssuer.Scheme, options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = adminAuthOptions.Issuer,
        ValidAudience = AdminTokenIssuer.Audience,
        IssuerSigningKey = adminTokenIssuer.SigningKey,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admin", policy => policy
        .AddAuthenticationSchemes(AdminTokenIssuer.Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim("typ", "admin"));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

var app = builder.Build();

if (string.IsNullOrWhiteSpace(smtpOptions.Host))
{
    app.Logger.LogWarning(
        "No Smtp:Host is configured, so sign-in codes and password resets cannot be delivered and " +
        "nobody can sign in. Bring up the mailpit service, or point Smtp:Host at a real server.");
}

// The Blazor bundle is fingerprinted at build time, so it is served from the asset manifest
// (MapStaticAssets) rather than off disk. UseStaticFiles stays for media uploaded at runtime.

// Nothing here is fingerprinted, so with no Cache-Control a CDN in front invents one — often
// hours for .css, which serves a stale stylesheet after a deploy and fails the service worker's
// integrity check on the new bundle, so the whole offline cache silently stops updating.
// "no-cache" still caches; it just forces a revalidation against the ETag.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapAuth();
app.MapGame();
app.MapAdminAuth();
app.MapAdminAccounts();
app.MapAdmin();
app.MapFallbackToFile("index.html");

// --- start-up work -------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await scope.ServiceProvider.GetRequiredService<MongoContext>().EnsureIndexesAsync();
        await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync(app.Environment.ContentRootPath);

        // Bootstrap: an install with no administrator has no way in, so make one and say so loudly.
        var admins = scope.ServiceProvider.GetRequiredService<IAdminUserRepository>();
        if (await admins.CountAsync() == 0)
        {
            var generated = string.IsNullOrWhiteSpace(adminAuthOptions.BootstrapPassword);
            var password = generated
                ? "quesshi-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
                : adminAuthOptions.BootstrapPassword!;

            await scope.ServiceProvider.GetRequiredService<AdminAuthService>()
                .CreateAsync(adminAuthOptions.BootstrapUsername, adminAuthOptions.BootstrapEmail, password, mustChangePassword: generated);

            if (generated)
                logger.LogWarning("Created the first administrator: username \"{Username}\", password \"{Password}\" — sign in at /admin and change it.",
                    adminAuthOptions.BootstrapUsername, password);
            else
                logger.LogInformation("Created the first administrator \"{Username}\" from configuration.", adminAuthOptions.BootstrapUsername);
        }

        // A placeholder address cannot receive a reset link, which only matters once you need one.
        foreach (var stranded in (await admins.AllAsync()).Where(a => !EmailAddress.LooksValid(a.Email) || IsPlaceholderDomain(a.Email)))
            logger.LogWarning("Admin \"{Username}\" has an unreachable email ({Email}); password reset cannot get to it. Change it under Admin -> Change password.",
                stranded.Username, stranded.Email);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Start-up seeding failed — is Mongo running? The app will keep going.");
    }
}

// `dotnet run --project src/Quesshi.Server -- add-admin <username> <email> <password>`
// Creating an administrator otherwise requires being signed in as one, so this is the way back in
// when every password is lost. The silo only starts on app.Run, so this costs one Mongo round trip.
if (args is ["add-admin", var newUsername, var newEmail, var newPassword, ..])
{
    using var scope = app.Services.CreateScope();
    var admins = scope.ServiceProvider.GetRequiredService<IAdminUserRepository>();

    if (await admins.GetByUsernameAsync(newUsername) is not null)
    {
        Console.Error.WriteLine($"An administrator called \"{newUsername}\" already exists.");
        return 1;
    }

    if (!EmailAddress.LooksValid(newEmail))
    {
        Console.Error.WriteLine($"\"{newEmail}\" does not look like an email address, and it is the only way to reset a lost password.");
        return 1;
    }

    var problems = PasswordPolicy.Problems(newPassword);
    if (problems.Count > 0)
    {
        Console.Error.WriteLine($"That password will not do: {string.Join(", ", problems)}");
        return 1;
    }

    await scope.ServiceProvider.GetRequiredService<AdminAuthService>()
        .CreateAsync(newUsername, newEmail, newPassword, mustChangePassword: true);

    Console.WriteLine($"Created administrator \"{newUsername}\". It must change its password on first sign-in.");
    return 0;
}

// `dotnet run --project src/Quesshi.Server -- approve-ai`
// Publishes the backlog of generated questions that were parked for review under the old
// review-everything-first policy. Questions an admin explicitly rejected are left rejected.
if (args is ["approve-ai", ..])
{
    using var scope = app.Services.CreateScope();
    var questions = scope.ServiceProvider.GetRequiredService<IQuestionRepository>();

    var approved = 0;
    while (true)
    {
        var batch = await questions.FindAsync(new QuestionFilter(Status: QuestionStatus.Pending, Take: 200));
        var generated = batch.Where(q => q.Source == QuestionSource.Ai && q.ReportCount == 0).ToList();
        if (generated.Count == 0) break;

        foreach (var question in generated) question.Approve();
        await questions.UpsertManyAsync(generated);
        approved += generated.Count;

        // Everything left pending in this page is not ours to touch, so stop rather than spin.
        if (generated.Count < batch.Count) break;
    }

    Console.WriteLine($"Approved {approved} generated questions.");
    return 0;
}


app.Run();
return 0;


public partial class Program
{
    /// <summary>Addresses that resolve only on this machine, so no reset link can ever arrive.</summary>
    private static bool IsPlaceholderDomain(string email)
        => email.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        || email.EndsWith("@localhost", StringComparison.OrdinalIgnoreCase);
}
