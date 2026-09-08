using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Infrastructure;
using Quesshi.Server.Auth;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>
/// Hosts the real <see cref="LobbyHub"/> at <c>/hub/lobby</c> behind the real authentication, against a
/// <see cref="FakePresence"/> and <see cref="LiveClusterFixture"/>'s already-running silo — the same
/// reasoning <see cref="AuthTestHost"/> and <see cref="LiveApiTestHost"/> give for building their own
/// host rather than WebApplicationFactory. The cluster is what lets QueueRandom/LeaveQueue and the
/// pending-challenge check the hub runs on connect reach the real <c>ILiveLobbyGrain</c>; everything
/// the hub needs beyond presence and that grain is faked.
/// </summary>
public sealed class LobbyHubTestHost : IAsyncDisposable
{
    public const string SigningKey = "a-lobby-hub-test-signing-key-long-enough-here";

    public TokenIssuer TokenIssuer { get; } = new(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 });
    public FakePresence Presence { get; } = new();

    private readonly IHost _host;
    private readonly TestServer _server;

    public LobbyHubTestHost(TestCluster cluster)
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddSignalR();
                    services.AddQuesshiAuthentication(
                        new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 },
                        new AdminAuthOptions { Key = "unused-admin-key-long-enough-here", Issuer = "quesshi" },
                        TokenIssuer,
                        new AdminTokenIssuer(new AdminAuthOptions { Key = "unused-admin-key-long-enough-here", Issuer = "quesshi" }));
                    services.AddSingleton<IPresence>(Presence);
                    services.AddSingleton(cluster.GrainFactory);
                    services.AddSingleton<ILobbyNotifier, FakeLobbyNotifier>();
                    services.AddSingleton<IPlayerRepository, FakePlayers>();
                    services.AddSingleton<IIdFactory, IdFactory>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapHub<LobbyHub>("/hub/lobby"));
                });
            })
            .Start();

        _server = _host.GetTestServer();
    }

    /// <summary>
    /// TestServer has no real socket, so this rides long polling over the test handler — plain HTTP,
    /// which TestServer supports directly, unlike a WebSocket upgrade.
    /// </summary>
    public HubConnection NewConnection(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_server.BaseAddress, "/hub/lobby"), options =>
        {
            options.HttpMessageHandlerFactory = _ => _server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult(token);
        })
        .Build();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it is true or <paramref name="timeout"/> elapses. For
    /// waits that have no callback to hook — <c>OnDisconnectedAsync</c> awaits <c>MarkOfflineAsync</c>
    /// before <c>Queue.LeaveAsync</c>, so a presence signal alone does not prove the grain's queue entry
    /// is gone — this reads the grain's own state instead of guessing how long that takes.
    /// </summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }

        if (!await condition())
            throw new TimeoutException($"Timed out after {timeout} waiting for {because}.");
    }
}
