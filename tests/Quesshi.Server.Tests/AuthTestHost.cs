using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quesshi.Infrastructure;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;
using Quesshi.Server.Tenants;

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

    private readonly TenantContext _tenant = new();
    public TokenIssuer TokenIssuer { get; }
    public AdminTokenIssuer AdminTokenIssuer { get; }

    private readonly IHost _host;
    public HttpClient Client { get; }

    public AuthTestHost()
    {
        var jwtOptions = new JwtOptions { Key = ValidKey, Issuer = "quesshi", Audience = "quesshi", Days = 30 };
        var adminOptions = new AdminAuthOptions { Key = AdminKey, Issuer = "quesshi" };
        TokenIssuer = new TokenIssuer(jwtOptions, _tenant);
        AdminTokenIssuer = new AdminTokenIssuer(adminOptions, _tenant);
        var tenants = new TenantRegistry(new TenantOptions
        {
            Tenants =
            [
                new TenantDefinition { Id = "quesshi", Name = "Quesshi", Hosts = ["localhost"], Theme = "red", EnabledModes = ["async", "live", "voting"] },
                new TenantDefinition
                {
                    Id = "brand-a", Name = "Brand A", Hosts = ["brand-a.test"], Theme = "blue",
                    LandingContent = new() { ["en"] = "Brand welcome" },
                    Languages = ["fa", "en", "nl"], EnabledModes = ["live"],
                    Rules = new() { ["maxPlayers"] = "8" }
                }
            ]
        });

        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization(options => options.AddPolicy("admin", policy =>
                        policy.AddAuthenticationSchemes(AdminTokenIssuer.Scheme).RequireAuthenticatedUser()));
                    services.AddSingleton(_tenant);
                    services.AddSingleton(tenants);
                    services.AddQuesshiAuthentication(jwtOptions, adminOptions, TokenIssuer, AdminTokenIssuer);
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<TenantResolutionMiddleware>();
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

                        endpoints.MapGet("/api/admin/probe", () => Results.Ok())
                            .RequireAuthorization("admin");

                        endpoints.MapTenantSettings();
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

    public IDisposable EnterTenant(string tenantId) => _tenant.Enter(tenantId);
}
