using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quesshi.Application.Ports;
using Quesshi.Server.Auth;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>
/// Hosts the real <see cref="LobbyHub"/> at <c>/hub/lobby</c> behind the real authentication, against a
/// <see cref="FakePresence"/> — no Orleans, no Mongo, no Redis, the same reasoning <see cref="AuthTestHost"/>
/// and <see cref="LiveApiTestHost"/> give for building their own host rather than WebApplicationFactory.
/// </summary>
public sealed class LobbyHubTestHost : IAsyncDisposable
{
    public const string SigningKey = "a-lobby-hub-test-signing-key-long-enough-here";

    public TokenIssuer TokenIssuer { get; } = new(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 });
    public FakePresence Presence { get; } = new();

    private readonly IHost _host;
    private readonly TestServer _server;

    public LobbyHubTestHost()
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
}
