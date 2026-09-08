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
using Quesshi.Shared;

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

    /// <summary>
    /// Every test class that stands up its own <see cref="LiveApiTestHost"/> shares the same
    /// <see cref="LiveShared.Archive"/> and grain factory, so two hosts minting ids from the same
    /// zero-seeded <c>FakeIdFactory</c> could collide on the same match id or share code and end up
    /// reading each other's grain activation — deterministically, not just under parallel test
    /// execution. Each host claims its own disjoint range instead.
    /// </summary>
    private static int _idSeed;

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
                services.AddSingleton<IIdFactory>(new FakeIdFactory(Interlocked.Add(ref _idSeed, 100_000)));
                services.AddSingleton<QuestionSetBuilder>();
                services.AddSignalR();
                services.AddSingleton<ILiveNotifier, SignalRLiveNotifier>();

                // AddQuesshiAuthentication above only needs a TokenIssuer to configure JWT validation
                // with, not to hand callers one to mint a fresh token with — the guest-join route
                // mapped below takes TokenIssuer as an ordinary DI parameter, so it has to be
                // resolvable from the container too. A fresh instance, not the TokenIssuer property:
                // a field initializer cannot reference another instance member (CS0236), and it need
                // not be the literal same object anyway — every TokenIssuer built from this SigningKey
                // mints and validates identically.
                services.AddSingleton(new TokenIssuer(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 }));
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

                    // Only this one guest route, not the full AuthEndpoints.MapAuth() — minimal API
                    // infers every mapped route's parameter sources (body/service/route) when the
                    // endpoint data source is first built, not lazily per request, so mapping the OTP
                    // and Google routes too would require registering AuthService/AuthOptions this
                    // host has no use for, just to satisfy inference for routes nothing here calls.
                    endpoints.MapPost("/api/auth/guest/live/{code}", async (string code, GuestJoinDto body,
                        IMatchArchive archive, IPlayerRepository players, IGrainFactory grains,
                        IQuestionRepository questions, ICategoryRepository categories, TokenIssuer tokens,
                        IIdFactory ids, IClock clock) =>
                        await AuthEndpoints.GuestJoinLiveAsync(code, body, archive, players, grains, questions, categories, tokens, ids, clock));
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
