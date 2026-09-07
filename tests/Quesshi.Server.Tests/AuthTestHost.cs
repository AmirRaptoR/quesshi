using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// Registers exactly the authentication AddQuesshiAuthentication wires into Program.cs, against two
/// bare endpoints that stand in for "a hub" and "an ordinary HTTP endpoint" — enough to prove the
/// query-string token hook behaves without needing Orleans, Mongo or Redis running. WebApplicationFactory
/// requires those (Program.cs makes Redis mandatory for clustering), so this builds its own host instead.
/// </summary>
public sealed class AuthTestHost : IAsyncDisposable
{
    public const string ValidKey = "a-test-signing-key-long-enough-to-use-here";
    public const string AdminKey = "a-different-admin-signing-key-also-long-enough";

    public TokenIssuer TokenIssuer { get; } = new(new JwtOptions { Key = ValidKey, Issuer = "quesshi", Audience = "quesshi", Days = 30 });
    public AdminTokenIssuer AdminTokenIssuer { get; } = new(new AdminAuthOptions { Key = AdminKey, Issuer = "quesshi" });

    private readonly IHost _host;
    public HttpClient Client { get; }

    public AuthTestHost()
    {
        var jwtOptions = new JwtOptions { Key = ValidKey, Issuer = "quesshi", Audience = "quesshi", Days = 30 };
        var adminOptions = new AdminAuthOptions { Key = AdminKey, Issuer = "quesshi" };

        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddQuesshiAuthentication(jwtOptions, adminOptions, TokenIssuer, AdminTokenIssuer);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        // Stands in for the hub connection: any path under /hub is where the
                        // query-string token is meant to work.
                        endpoints.MapGet("/hub/live/probe", (HttpContext ctx) =>
                            Results.Ok(new { playerId = ctx.User.Identity!.Name ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value }))
                            .RequireAuthorization();

                        // Stands in for an ordinary HTTP endpoint, same shape as GameEndpoints.
                        endpoints.MapGet("/api/probe", (HttpContext ctx) =>
                            Results.Ok(new { playerId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value }))
                            .RequireAuthorization();
                    });
                });
            })
            .Start();

        Client = _host.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
