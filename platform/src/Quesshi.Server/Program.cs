using Quesshi.Application;
using Quesshi.Application.Content;
using Quesshi.Application.Identity;
using Quesshi.Application.Jobs;
using Quesshi.Features.SignIn.Google;
using Quesshi.Infrastructure.Capabilities;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Features;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server.Commands;
using Quesshi.Server.Health;
using Quesshi.Server.Accounts;
using Quesshi.Server.Jobs;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;
using Quesshi.Engine.Contracts;
using Quesshi.Application.Sessions;
using Quesshi.Formats;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Server.Sessions;
using Quesshi.Shared;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Otp;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Streaming;
using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.HttpOverrides;

namespace Quesshi.Server;

public static class Program
{
    public static Task<int> Main(string[] args) => DispatchAsync(args);

    internal static async Task<int> DispatchAsync(
        string[] args,
        Func<Task>? serve = null,
        Func<Task>? migrate = null,
        Func<Task<int>>? sync = null,
        Func<IReadOnlyList<string>, Task>? tenantAdd = null,
        Func<AdminAddOptions, Task>? adminAdd = null)
    {
        try
        {
            switch (args)
            {
                case ["migrate"]:
                    await (migrate ?? (() => MigrateAsync(Environment.GetEnvironmentVariable("ConnectionStrings__Owner"))))();
                    return 0;
                case ["sync"]:
                    await (sync ?? (() => SyncAsync()))();
                    return 0;
                case ["tenant", "add", .. var tenantArgs]:
                    await (tenantAdd ?? (async values => _ = await AddTenantAsync(values)))(tenantArgs);
                    return 0;
                case ["admin", "add", .. var adminArgs]:
                    var adminOptions = AdminAddOptions.Parse(adminArgs);
                    await (adminAdd ?? (async options => _ = await AddAdminAsync(options)))(adminOptions);
                    return 0;
                case []:
                case ["serve"]:
                    await (serve ?? (() => ServeAsync(args)))();
                    return 0;
                default:
                    throw new CommandException("Expected 'serve', 'migrate', 'sync', 'tenant add', or 'admin add'.");
            }
        }
        catch (CommandException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (InvalidOperationException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }
    }

    public static async Task MigrateAsync(string? ownerConnectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerConnectionString))
        {
            throw new InvalidOperationException("ConnectionStrings__Owner is required for the migrate command.");
        }

        var migrations = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly);
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await new MigrationRunner(migrations).RunAsync(connection, cancellationToken);
    }

    internal static async Task<int> SyncAsync(DatabaseSources? sources = null, CapabilityRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        if (sources is null)
        {
            await using var environmentSources = DatabaseSources.FromEnvironment();
            return await SyncAsync(environmentSources, registry, cancellationToken);
        }

        return await new CapabilitySyncCommand(new SystemDb(sources), registry ?? CreateCapabilityRegistry())
            .ExecuteAsync(cancellationToken);
    }

    internal static async Task<Guid> AddTenantAsync(IReadOnlyList<string> args, DatabaseSources? sources = null, CapabilityRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        var options = TenantAddOptions.Parse(args);
        if (sources is null)
        {
            await using var environmentSources = DatabaseSources.FromEnvironment();
            return await AddTenantAsync(args, environmentSources, registry, cancellationToken);
        }

        return await new TenantAddCommand(new SystemDb(sources), registry ?? CreateCapabilityRegistry())
            .ExecuteAsync(options, cancellationToken);
    }

    internal static async Task<Guid> AddAdminAsync(IReadOnlyList<string> args, DatabaseSources? sources = null, CancellationToken cancellationToken = default)
    {
        var options = AdminAddOptions.Parse(args);
        return await AddAdminAsync(options, sources, cancellationToken);
    }

    private static async Task<Guid> AddAdminAsync(AdminAddOptions options, DatabaseSources? sources = null, CancellationToken cancellationToken = default)
    {
        if (sources is null)
        {
            await using var environmentSources = DatabaseSources.FromEnvironment();
            return await AddAdminAsync(options, environmentSources, cancellationToken);
        }

        return await new AdminAddCommand(new AdminAccountCreator(new SystemDb(sources), new IdentityPasswordHasher()))
            .ExecuteAsync(options, cancellationToken);
    }

    internal static Task BootstrapTenantAdminsAsync(
        DatabaseSources sources,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        new TenantAdminBootstrapper(
            new SystemDb(sources),
            new AdminAccountCreator(new SystemDb(sources), new IdentityPasswordHasher()))
        .EnsureAllAsync(output, cancellationToken);

    internal static WebApplication CreateApplication(
        DatabaseSources? configuredSources = null,
        int? siloPort = null,
        int? gatewayPort = null,
        Action<ILoggingBuilder>? configureLogging = null,
        FeatureRegistry? configuredFeatures = null,
        IFormatRegistry? configuredFormats = null,
        IKindRegistry? configuredKinds = null,
        ISessionTurnObserver? turnObserver = null,
        string? environmentName = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var options = new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = environmentName
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseStaticWebAssets();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        configureLogging?.Invoke(builder.Logging);

        var sources = configuredSources ?? DatabaseSources.FromEnvironment();
        builder.Services.AddSingleton(sources);
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IDatabaseClock, PostgresDatabaseClock>();
        builder.Services.AddSingleton<PostgresClock>();
        builder.Services.AddSingleton<IClock>(provider => provider.GetRequiredService<PostgresClock>());
        builder.Services.AddSingleton<IClockHealth>(provider => provider.GetRequiredService<PostgresClock>());
        builder.Services.AddSingleton<IBackgroundLoopRegistry, BackgroundLoopRegistry>();
        builder.Services.AddSingleton<IPlatformDatabaseHealth, PlatformDatabaseHealth>();
        builder.Services.AddSingleton<IPlatformSiloHealth, PlatformSiloHealth>();
        builder.Services.AddSingleton<PlatformHealth>();
        builder.Services.AddTenantResolution();
        builder.Services.AddPlayerSecurity();
        builder.Services.AddAdminServices();
        builder.Services.TryAddSingleton<ITenantDb, TenantDb>();
        builder.Services.AddSingleton<ICategoryService, CategoryService>();
        builder.Services.AddSingleton<IContent, ContentService>();
        builder.Services.AddSingleton<IGoogleSignInSettings, GoogleSignInSettingsProvider>();
        builder.Services.AddSingleton<IExternalSignInContext, ExternalSignInContext>();
        builder.Services.AddSingleton<IIdentity>(provider => new ExternalIdentityService(
            provider.GetRequiredService<DatabaseSources>(),
            provider.GetRequiredService<ITenantDb>(),
            EmailAuthService.SecretFromEnvironment(),
            TimeSpan.FromSeconds(provider.GetRequiredService<IConfiguration>().GetValue(
                "EmailAuth:ProfileTokenLifetimeSeconds",
                (int)EmailAuthOptions.Default.ProfileTokenLifetime.TotalSeconds)),
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddHttpClient<GoogleIdTokenValidator>(client => client.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<GoogleKeySetCache>();
        builder.Services.AddSingleton<ISessionSnapshotLoader, SessionSnapshotLoader>();
        builder.Services.AddSingleton<SessionStateStore>();
        builder.Services.AddSingleton<IViewRenderer, ViewRenderer>();
        builder.Services.AddSingleton<PostgresStreamNotificationListener>();
        builder.Services.AddSingleton<IStreamNotificationSource>(provider => provider.GetRequiredService<PostgresStreamNotificationListener>());
        builder.Services.AddHostedService(provider => provider.GetRequiredService<PostgresStreamNotificationListener>());
        builder.Services.AddSingleton<IStreamSessionLiveness, StreamSessionLiveness>();
        builder.Services.AddSingleton<StreamHub>();
        builder.Services.AddSingleton<IAccountDeletionNotifier>(provider => provider.GetRequiredService<StreamHub>());
        builder.Services.AddHostedService(provider => provider.GetRequiredService<StreamHub>());
        builder.Services.AddSingleton<ISessionDeadlineReconciler, NoopSessionDeadlineReconciler>();
        builder.Services.AddSingleton<ISessionTurnObserver>(turnObserver ?? NullSessionTurnObserver.Instance);
        builder.Services.AddSingleton<IDueSessionSource, PostgresDueSessionSource>();
        builder.Services.AddSingleton<ISessionTimeoutDispatcher, OrleansSessionTimeoutDispatcher>();
        builder.Services.AddSingleton<DeadlineSweep>();
        builder.Services.AddHostedService<DeadlineSweepService>();
        builder.Services.AddSingleton<IFormatRegistry>(_ => configuredFormats ?? PlatformFormats.CreateRegistry());
        builder.Services.AddSingleton<IKindRegistry>(_ => configuredKinds ?? new KindRegistry([new ChoiceKind(), new PollChoiceKind(), new PollRosterKind()]));
        builder.Services.AddSingleton(provider =>
        {
            var registry = configuredFeatures ?? FeatureRegistry.Create(FeatureRegistry.Discover(typeof(FeaturesMarker).Assembly));
            registry.Jobs.Register("core", "event", (context, cancellationToken) =>
                provider.GetRequiredService<DurableEvents>().HandleEventJobAsync(context, cancellationToken));
            registry.Jobs.Register("core", "cleanup-player", (context, cancellationToken) =>
                provider.GetRequiredService<DurableEvents>().HandleCleanupJobAsync(context, cancellationToken));
            registry.Jobs.Register("core", "player-cleanup", (context, cancellationToken) =>
                provider.GetRequiredService<CorePlayerCleanup>().HandleAsync(context, cancellationToken));
            // Keep already-persisted account-deletion cleanup jobs runnable while new jobs use
            // the shared player-cleanup kind.
            registry.Jobs.Register("core", "account-player-cleanup", (context, cancellationToken) =>
                provider.GetRequiredService<CorePlayerCleanup>().HandleAsync(context, cancellationToken));
            registry.Jobs.Register("core", "guest-purge", (context, cancellationToken) =>
                provider.GetRequiredService<GuestPurge>().HandleAsync(context, cancellationToken));
            registry.Jobs.Register("core", "guest-remove-player", (context, cancellationToken) =>
                provider.GetRequiredService<GuestPlayerRemoval>().HandleAsync(context, cancellationToken));
            registry.Schedules.Every("core", TimeSpan.FromDays(1), "guest-purge");
            return registry;
        });
        builder.Services.AddSingleton<SignInRegistry>(provider => SignInRegistry.Create(
            provider.GetRequiredService<FeatureRegistry>().Capabilities,
            SignInRegistry.Discover(typeof(FeaturesMarker).Assembly)));
        builder.Services.AddSingleton(provider =>
        {
            var features = provider.GetRequiredService<FeatureRegistry>();
            _ = provider.GetRequiredService<SignInRegistry>();
            return features.Capabilities;
        });
        builder.Services.AddSingleton(provider => provider.GetRequiredService<FeatureRegistry>().Jobs);
        builder.Services.AddSingleton(provider => provider.GetRequiredService<FeatureRegistry>().Schedules);
        builder.Services.TryAddSingleton<ICapabilities, TenantCapabilities>();
        builder.Services.TryAddSingleton<ILocks, TenantLocks>();
        builder.Services.TryAddSingleton<IJobs, TenantJobs>();
        builder.Services.TryAddSingleton<ISessionEffectsNotifier, NullSessionEffectsNotifier>();
        builder.Services.AddSingleton<DurableEvents>();
        builder.Services.AddSingleton<IEvents>(provider => provider.GetRequiredService<DurableEvents>());
        builder.Services.AddSingleton<IPlayerCleanup>(provider => provider.GetRequiredService<DurableEvents>());
        builder.Services.AddSingleton<ISessionEffects>(provider => provider.GetRequiredService<DurableEvents>());
        builder.Services.AddSingleton<CorePlayerCleanup>();
        builder.Services.AddSingleton<GuestPurge>(provider => new GuestPurge(
            provider.GetRequiredService<ILocks>(),
            provider.GetRequiredService<CorePlayerCleanup>(),
            provider.GetRequiredService<IJobs>(),
            new SystemDb(sources),
            provider.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<GuestPlayerRemoval>(provider => new GuestPlayerRemoval(
            provider.GetRequiredService<ILocks>(), new SystemDb(sources)));
        builder.Services.AddSingleton<AccountDeletionService>(provider => new AccountDeletionService(
            new SystemDb(sources), provider.GetRequiredService<IAccountDeletionNotifier>()));
        if (builder.Configuration.GetValue("Jobs:Enabled", true))
        {
            builder.Services.AddSingleton<IJobStore, PostgresJobStore>();
            builder.Services.AddSingleton<IJobScheduleTenants, PostgresJobScheduleTenants>();
            builder.Services.AddSingleton<SystemJobs>();
            builder.Services.AddSingleton<ISystemJobs>(provider => provider.GetRequiredService<SystemJobs>());
            builder.Services.AddSingleton<ISystemScheduledJobs>(provider => provider.GetRequiredService<SystemJobs>());
            builder.Services.AddSingleton<JobWorker>();
            builder.Services.AddSingleton<JobScheduler>();
            builder.Services.AddHostedService<JobWorkerService>();
            builder.Services.AddHostedService<JobScheduleService>();
        }
        builder.Host.UseOrleans(silo =>
        {
            var membership = new NpgsqlConnectionStringBuilder(sources.SystemConnectionString) { SearchPath = "orleans" }.ConnectionString;
            silo.Configure<ClusterOptions>(cluster => { cluster.ClusterId = "quesshi"; cluster.ServiceId = "quesshi"; });
            if (siloPort is not null && gatewayPort is not null)
            {
                silo.ConfigureEndpoints(IPAddress.Loopback, siloPort.Value, gatewayPort.Value, listenOnAnyHostAddress: false);
            }
            else
            {
                silo.ConfigureEndpoints(11111, 30000);
            }
            silo.UseAdoNetClustering(configuration =>
            {
                configuration.Invariant = "Npgsql";
                configuration.ConnectionString = membership;
            });
        });

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapGet("/health", async (PlatformHealth health, CancellationToken cancellationToken) =>
        {
            var report = await health.CheckAsync(cancellationToken);
            return report.IsHealthy ? Results.Ok(report) : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapTenantResources();
        app.MapRoutes();
        app.MapStream();
        app.UseForwardedHeaders();
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), tenant => tenant.UseTenantResolution());
        app.UsePlayerSessionAuthentication();
        app.UseAdminSessionAuthentication();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
        });
        // The Blazor client is published through static web assets. Its index remains tenant-neutral;
        // tenant branding and configuration are served by the host-scoped endpoints above.
        var staticAssetsManifest = GetStaticAssetsManifestPath();
        if (staticAssetsManifest is not null)
        {
            app.MapStaticAssets(staticAssetsManifest);
        }
        app.MapFallbackToFile("index.html");
        app.Use(async (context, next) =>
        {
            var tenant = TenantResolutionMiddleware.GetTenant(context);
            using (app.Logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenant?.Id,
                ["session_id"] = context.Items.TryGetValue("session_id", out var session) ? session : null
            })) await next();
        });
        return app;
    }

    private static CapabilityRegistry CreateCapabilityRegistry()
    {
        var features = FeatureRegistry.Create(FeatureRegistry.Discover(typeof(FeaturesMarker).Assembly));
        _ = SignInRegistry.Create(features.Capabilities, SignInRegistry.Discover(typeof(FeaturesMarker).Assembly));
        return features.Capabilities;
    }

    internal static string? GetStaticAssetsManifestPath(string? baseDirectory = null)
    {
        var manifestPath = Path.Combine(
            baseDirectory ?? AppContext.BaseDirectory,
            $"{typeof(Program).Assembly.GetName().Name}.staticwebassets.endpoints.json");

        return File.Exists(manifestPath) ? manifestPath : null;
    }

    private static async Task ServeAsync(string[] args)
    {
        await using var sources = DatabaseSources.FromEnvironment();
        await ServeAsync(sources);
    }

    internal static async Task ServeAsync(
        DatabaseSources sources,
        Func<WebApplication, Task>? run = null,
        CancellationToken cancellationToken = default,
        TextWriter? bootstrapOutput = null)
    {
        var migrations = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly);
        await using (var connection = await sources.System.OpenConnectionAsync(cancellationToken))
        {
            await new MigrationRunner(migrations).EnsureCurrentAsync(connection, cancellationToken);
        }

        await BootstrapTenantAdminsAsync(sources, bootstrapOutput ?? Console.Out, cancellationToken);

        await using var app = CreateApplication(sources);
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        await (run ?? (application => application.RunAsync(cancellationToken)))(app);
    }
}
