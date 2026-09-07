using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;
using Quesshi.Server.Hubs;

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
                services.AddSignalR();
                services.AddSingleton<ILiveNotifier, SignalRLiveNotifier>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapLive();
                    endpoints.MapHub<LiveHub>("/hub/live");
                });
            });
        })
        .Start();

    public HttpClient NewClient() => _host.GetTestServer().CreateClient();

    /// <summary>
    /// A real <see cref="HubConnection"/> against the in-memory <see cref="TestServer"/> — LongPolling
    /// is forced because the test server has no real socket to upgrade. Not started; the caller owns
    /// the lifecycle the same way <see cref="Quesshi.Web.Services.LiveClient"/>'s tests already do.
    /// </summary>
    public HubConnection NewHubConnection(string token) => new HubConnectionBuilder()
        .WithUrl("http://localhost/hub/live", options =>
        {
            options.HttpMessageHandlerFactory = _ => _host.GetTestServer().CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
