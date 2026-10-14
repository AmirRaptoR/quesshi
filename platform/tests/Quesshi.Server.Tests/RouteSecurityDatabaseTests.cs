using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class RouteSecurityDatabaseTests(PostgresFixture fixture)
{
    private const string TenantHost = "security.example.test";

    [SkippableFact]
    public async Task Player_and_open_routes_enforce_identity_capability_origin_and_antiforgery()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11145, 30145, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:50145");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50145") };
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var accountContext = new DefaultHttpContext();
            await sessions.IssueAccountAsync(accountContext, seeded.TenantId, seeded.AccountId);
            var accountCookie = CookieValue(accountContext);

            var anonymousToken = await AntiforgeryAsync(client, null);
            var accountToken = await AntiforgeryAsync(client, accountCookie);

            using (var noToken = await SendAsync(client, HttpMethod.Post, "/api/x/security/open/json", null, null, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            }
            using (var foreignOrigin = await SendAsync(client, HttpMethod.Post, "/api/x/security/open/json", accountCookie, accountToken, origin: "https://foreign.example.test"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
            }
            using (var staleIdentity = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", accountCookie, anonymousToken, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.BadRequest, staleIdentity.StatusCode);
            }
            using (var noPlayer = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", null, anonymousToken, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, noPlayer.StatusCode);
            }
            using (var player = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", accountCookie, accountToken, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.OK, player.StatusCode);
            }
            using (var multipart = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/multipart", accountCookie, accountToken, origin: TenantOrigin,
                       content: new MultipartFormDataContent { { new StringContent("file"), "upload" } }))
            {
                Assert.Equal(HttpStatusCode.OK, multipart.StatusCode);
            }

            using (var signOut = await SendAsync(client, HttpMethod.Post, "/api/auth/sign-out", accountCookie, accountToken, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
                Assert.Contains(signOut.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-qp=", StringComparison.Ordinal));
            }
            using (var revoked = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", accountCookie, accountToken, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            }

            var guestContext = new DefaultHttpContext();
            await sessions.IssueGuestAsync(guestContext, seeded.TenantId, seeded.GuestId);
            var guestCookie = CookieValue(guestContext);
            var guestToken = await AntiforgeryAsync(client, guestCookie);
            using (var guestDenied = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", guestCookie, guestToken, origin: TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.Forbidden, guestDenied.StatusCode);
            }
            using (var guestAllowed = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/multipart", guestCookie, guestToken, origin: TenantOrigin,
                       content: new MultipartFormDataContent { { new StringContent("file"), "upload" } }))
            {
                Assert.Equal(HttpStatusCode.OK, guestAllowed.StatusCode);
            }

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            await owner.ExecuteAsync("""
                update tenant_capabilities set enabled = false
                where tenant_id = @tenantId and type = 'feature' and key = 'security'
                """, new { tenantId = seeded.TenantId });
            using var disabled = await SendAsync(client, HttpMethod.Post, "/api/x/security/open/json", guestCookie, guestToken, origin: TenantOrigin);
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Account_deletion_requires_an_account_session_and_clears_its_cookie()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        await using var app = Program.CreateApplication(sources, 11146, 30146);
        app.Urls.Add("http://127.0.0.1:50146");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50146") };
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var accountContext = new DefaultHttpContext();
            await sessions.IssueAccountAsync(accountContext, seeded.TenantId, seeded.AccountId);
            var accountCookie = CookieValue(accountContext);
            var accountToken = await AntiforgeryAsync(client, accountCookie);

            var guestContext = new DefaultHttpContext();
            await sessions.IssueGuestAsync(guestContext, seeded.TenantId, seeded.GuestId);
            var guestCookie = CookieValue(guestContext);
            var guestToken = await AntiforgeryAsync(client, guestCookie);

            using (var anonymous = await SendAsync(client, HttpMethod.Delete, "/api/me", null, await AntiforgeryAsync(client, null), TenantOrigin))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var guest = await SendAsync(client, HttpMethod.Delete, "/api/me", guestCookie, guestToken, TenantOrigin))
                Assert.Equal(HttpStatusCode.Forbidden, guest.StatusCode);
            using (var missingToken = await SendAsync(client, HttpMethod.Delete, "/api/me", accountCookie, null, TenantOrigin))
                Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
            using (var deleted = await SendAsync(client, HttpMethod.Delete, "/api/me", accountCookie, accountToken, TenantOrigin))
            {
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                Assert.Contains(deleted.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-qp=", StringComparison.Ordinal) && value.Contains("max-age=0", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string TenantOrigin => $"http://{TenantHost}";

    private async Task<(Guid TenantId, Guid AccountId, Guid GuestId)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            delete from tenant_hosts where host = @host;
            insert into tenants(id, slug, name, default_language) values (@tenantId, @slug, 'Security', 'en');
            insert into tenant_hosts(tenant_id, host) values (@tenantId, @host);
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, 'Account player', 'account', 'en'),
                   (@tenantId, @guestId, null, 'Guest player', 'guest', 'en');
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            values (@tenantId, 'feature', 'security', true, true);
            """, new { tenantId, slug = $"security-{tenantId:N}", host = TenantHost, accountId, email = $"security-{accountId:N}@example.test", playerId, guestId });
        return (tenantId, accountId, guestId);
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, string? cookie)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/antiforgery", cookie, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store, no-cache, max-age=0", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookie,
        string? csrf,
        string? origin = null,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = TenantHost;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qp={cookie}");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF", csrf);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static string CookieValue(DefaultHttpContext context)
    {
        var header = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(header);
        var value = header["__Host-qp=".Length..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private sealed record AntiforgeryResponse(string Token);

    private sealed class SecurityRoutesFeature : IFeature
    {
        public string Id => "security";

        public FeatureSettings Settings => FeatureSettings.Create(new SecuritySettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
            feature.OpenRoutes(routes => routes.MapPost("/open/json", () => Results.Ok()));
            feature.PlayerRoutes(routes =>
            {
                routes.MapPost("/player/json", () => Results.Ok());
                routes.MapPost("/player/multipart", async (HttpRequest request) =>
                {
                    var form = await request.ReadFormAsync();
                    return Results.Ok(new { count = form.Count });
                }).AllowGuests();
            });
        }
    }

    private sealed record SecuritySettings;
}
