using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// Hosts the real <c>MapLive()</c> endpoints (not a stand-in) behind the real authentication and
/// guest filter, against <see cref="LiveClusterFixture"/>'s already-running silo — so this proves the
/// guest gate actually denies at the HTTP layer, the one thing driving the handlers directly cannot.
/// WebApplicationFactory is still unusable (Redis clustering), so this builds its own host, the same
/// way <see cref="AuthTestHost"/> does for auth.
/// </summary>
public sealed class LiveApiTestHost(TestCluster cluster) : IAsyncDisposable
{
    public const string SigningKey = "a-live-endpoint-test-signing-key-long-enough";

    public TokenIssuer TokenIssuer { get; } = new(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 });

    private readonly IHost _host = new HostBuilder()
        .ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization();
                services.AddQuesshiAuthentication(
                    new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 },
                    new AdminAuthOptions { Key = "unused-admin-key-long-enough-here", Issuer = "quesshi" },
                    new TokenIssuer(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 }),
                    new AdminTokenIssuer(new AdminAuthOptions { Key = "unused-admin-key-long-enough-here", Issuer = "quesshi" }));

                services.AddSingleton(cluster.GrainFactory);
                services.AddSingleton<IQuestionRepository>(LiveShared.Questions);
                services.AddSingleton<ICategoryRepository>(LiveShared.Categories);
                services.AddSingleton<IMatchArchive>(LiveShared.Archive);
                services.AddSingleton<IPlayerRepository>(LiveShared.Players);
                services.AddSingleton<IClock>(new TimeProviderClock(LiveShared.TimeProvider));
                services.AddSingleton<IIdFactory>(new FakeIdFactory());
                services.AddSingleton<QuestionSetBuilder>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapLive());
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
