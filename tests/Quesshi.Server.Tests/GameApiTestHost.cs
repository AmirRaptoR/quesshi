using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Infrastructure;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// Hosts the real <c>MapGame()</c> endpoints (not a stand-in) behind the real authentication and guest
/// filter, against <see cref="ClusterFixture"/>'s already-running silo — the same shape
/// <see cref="LiveApiTestHost"/> and <see cref="AdminApiTestHost"/> already take for their own endpoint
/// groups. Issue #54's whole point is that <c>PUT /api/me</c> must both pass the guest gate and route
/// its write through <c>IPlayerGrain</c> — a handler called directly (the way most of this file's
/// sibling tests drive <c>GameEndpoints</c>) would skip the endpoint filter entirely and prove nothing
/// about the 403 this issue fixes. Issue #55 adds <c>POST /api/me/upgrade</c> to the same group, for
/// the same reason: the guest gate and the not-a-guest guard it adds are both wiring, not handler logic.
/// </summary>
public sealed class GameApiTestHost : IAsyncDisposable
{
    public const string SigningKey = "a-game-endpoint-test-signing-key-long-enough";

    public TokenIssuer TokenIssuer { get; } = new(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 });

    /// <summary>The guest-upgrade tests (issue #55) plant a challenge here directly rather than going
    /// through <c>POST /api/auth/otp/request</c>, which this host does not map — the same minimal-surface
    /// reasoning <see cref="LiveApiTestHost"/>'s own comment on <c>AuthTestHost</c> gives. An explicit
    /// constructor, rather than a field initializer, is what lets this be handed to the host's own
    /// <c>ConfigureServices</c> below: a field initializer cannot reference another instance member
    /// (CS0236), which <see cref="LiveApiTestHost"/> hits for the exact same reason over its own
    /// <c>TokenIssuer</c> property.</summary>
    public FakeOtpStore Otps { get; }

    private readonly IHost _host;

    public GameApiTestHost(TestCluster cluster)
    {
        Otps = new FakeOtpStore();

        _host = new HostBuilder()
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
                    services.AddSingleton<IQuestionRepository>(Shared.Questions);
                    services.AddSingleton<ICategoryRepository>(Shared.Categories);
                    services.AddSingleton<IMatchArchive>(Shared.Archive);
                    services.AddSingleton<IPlayerRepository>(Shared.Players);
                    services.AddSingleton<ILeaderboard>(Shared.Leaderboard);
                    services.AddSingleton<IPresence>(new FakePresence());
                    services.AddSingleton<IClock>(Shared.Clock);
                    services.AddSingleton<IIdFactory>(new IdFactory());
                    services.AddSingleton<QuestionSetBuilder>();

                    // POST /api/me/upgrade (issue #55) needs AuthService and a TokenIssuer resolvable
                    // from the container, same as every other minimal-API parameter here —
                    // AddQuesshiAuthentication above only configures JWT validation with a TokenIssuer,
                    // it does not register one for DI to hand back. A fresh instance built from the same
                    // SigningKey validates and mints identically to the TokenIssuer property, the same
                    // reasoning LiveApiTestHost's own comment on this exact duplication gives.
                    services.AddSingleton(new TokenIssuer(new JwtOptions { Key = SigningKey, Issuer = "quesshi", Audience = "quesshi", Days = 1 }));
                    services.AddSingleton<IOtpStore>(Otps);
                    services.AddSingleton<IOtpSender, FakeOtpSender>();
                    services.AddSingleton<AuthService>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGame());
                });
            })
            .Start();
    }

    public HttpClient NewClient() => _host.GetTestServer().CreateClient();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
