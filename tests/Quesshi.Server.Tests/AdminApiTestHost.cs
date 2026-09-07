using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Infrastructure.Generation;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// Hosts the real <c>MapAdmin()</c> endpoints behind the real admin authentication scheme and
/// policy, against <see cref="LiveClusterFixture"/>'s already-running silo — the same shape as
/// <see cref="LiveApiTestHost"/>, so the auth/authorisation criteria are proven at the HTTP layer
/// rather than by calling a handler directly.
/// </summary>
public sealed class AdminApiTestHost(TestCluster cluster) : IAsyncDisposable
{
    public const string PlayerSigningKey = "an-admin-endpoint-test-player-signing-key-long-enough";
    public const string AdminSigningKey = "an-admin-endpoint-test-admin-signing-key-long-enough";

    public TokenIssuer PlayerTokenIssuer { get; } = new(new JwtOptions { Key = PlayerSigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 });
    public AdminTokenIssuer AdminTokenIssuer { get; } = new(new AdminAuthOptions { Key = AdminSigningKey, Issuer = "quesshi" });

    private readonly IHost _host = new HostBuilder()
        .ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization();
                services.AddAuthorizationBuilder()
                    .AddPolicy("admin", policy => policy
                        .AddAuthenticationSchemes(AdminTokenIssuer.Scheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim("typ", "admin"));

                services.AddQuesshiAuthentication(
                    new JwtOptions { Key = PlayerSigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 },
                    new AdminAuthOptions { Key = AdminSigningKey, Issuer = "quesshi" },
                    new TokenIssuer(new JwtOptions { Key = PlayerSigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 }),
                    new AdminTokenIssuer(new AdminAuthOptions { Key = AdminSigningKey, Issuer = "quesshi" }));

                services.AddSingleton(cluster.GrainFactory);
                services.AddSingleton<IQuestionRepository>(LiveShared.Questions);
                services.AddSingleton<ICategoryRepository>(LiveShared.Categories);
                services.AddSingleton<IMatchArchive>(LiveShared.Archive);
                services.AddSingleton<IPlayerRepository>(LiveShared.Players);
                services.AddSingleton<ILiveDirectory>(LiveShared.Directory);
                services.AddSingleton<IClock>(new TimeProviderClock(LiveShared.TimeProvider));
                services.AddSingleton<IIdFactory>(new FakeIdFactory());
                services.AddSingleton<IGenerationLog, FakeGenerationLog>();
                services.AddSingleton<IAiSpendLog, FakeAiSpendLog>();
                services.AddSingleton<IQuestionGenerator, FakeQuestionGenerator>();
                services.AddSingleton(new TopUpOptions());
                services.AddSingleton(new OpenRouterOptions());
                services.AddSingleton<QuestionSetBuilder>();
                services.AddSingleton<TopUpQuestionBank>();
                services.AddLogging();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapAdmin());
            });
        })
        .Start();

    public HttpClient NewClient() => _host.GetTestServer().CreateClient();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
