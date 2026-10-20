using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class GuestUpgradeEndpointDatabaseTests(PostgresFixture fixture)
{
    // 10161-10164 and 30161-30164 are reserved for this class.
    private static int nextSiloPort = 10160;

    [SkippableFact]
    public async Task Failed_upgrade_codes_are_rejected_by_the_shared_dynamic_host()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var host = new PlatformHttpFixture([new GuestUpgradeHostFeature()]);
        await host.StartAsync();

        using var response = await host.Guest.PostAsJsonAsync("/api/me/upgrade", new
        {
            email = "failed-route@example.test",
            code = "000000"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Upgrade_rotates_every_guest_session_and_keeps_the_same_player()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await InsertCodeAsync("upgrade-route@example.test", "123456");
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = LocalAddress(app) };
        try
        {
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var firstContext = new DefaultHttpContext();
            var secondContext = new DefaultHttpContext();
            await sessions.IssueGuestAsync(firstContext, seeded.TenantId, seeded.GuestId);
            await sessions.IssueGuestAsync(secondContext, seeded.TenantId, seeded.GuestId);
            var firstCookie = CookieValue(firstContext);
            var secondCookie = CookieValue(secondContext);
            var csrf = await AntiforgeryAsync(client, seeded.Host, firstCookie);

            using var upgraded = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/me/upgrade", firstCookie, csrf, seeded.Origin,
                JsonContent.Create(new { email = "upgrade-route@example.test", code = "123456" }));
            Assert.Equal(HttpStatusCode.OK, upgraded.StatusCode);
            Assert.Equal("upgraded", (await upgraded.Content.ReadFromJsonAsync<StatusResponse>())!.Status);
            var accountCookie = CookieValue(upgraded);

            using (var oldFirst = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", firstCookie, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, oldFirst.StatusCode);
            using (var oldSecond = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", secondCookie, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, oldSecond.StatusCode);
            using (var account = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", accountCookie, null, null))
            {
                Assert.Equal(HttpStatusCode.OK, account.StatusCode);
                Assert.Equal(seeded.GuestId, (await account.Content.ReadFromJsonAsync<ProfileResponse>())!.Id);
            }

            await using var owner = await OwnerAsync();
            Assert.Equal(0L, await owner.QuerySingleAsync<long>("select count(*) from auth_sessions where tenant_id = @tenant and guest_id = @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
            Assert.Equal(1L, await owner.QuerySingleAsync<long>("select count(*) from auth_sessions where tenant_id = @tenant and account_id is not null", new { tenant = seeded.TenantId }));
            Assert.Equal(seeded.GuestId, await owner.QuerySingleAsync<Guid>("select id from players where tenant_id = @tenant and account_id is not null", new { tenant = seeded.TenantId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Failed_codes_do_not_attach_or_revoke_the_guest_session()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await CreateTenantAccountAsync(seeded.TenantId, "failed-route@example.test");
        await CreateTenantAccountAsync(seeded.TenantId, "expired-route@example.test");
        await InsertCodeAsync("failed-route@example.test", "123456");
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = LocalAddress(app) };
        try
        {
            var context = new DefaultHttpContext();
            await app.Services.GetRequiredService<IPlayerSessionIssuer>().IssueGuestAsync(context, seeded.TenantId, seeded.GuestId);
            var cookie = CookieValue(context);
            var csrf = await AntiforgeryAsync(client, seeded.Host, cookie);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/me/upgrade", cookie, csrf, seeded.Origin,
                    JsonContent.Create(new { email = "failed-route@example.test", code = "000000" }));
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
            using (var exhausted = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/me/upgrade", cookie, csrf, seeded.Origin,
                       JsonContent.Create(new { email = "failed-route@example.test", code = "123456" })))
                Assert.Equal(HttpStatusCode.Unauthorized, exhausted.StatusCode);
            await InsertCodeAsync("expired-route@example.test", "123456", DateTimeOffset.UtcNow.AddMinutes(-1));
            using (var expired = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/me/upgrade", cookie, csrf, seeded.Origin,
                       JsonContent.Create(new { email = "expired-route@example.test", code = "123456" })))
                Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            using (var stillGuest = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", cookie, null, null))
                Assert.Equal(HttpStatusCode.OK, stillGuest.StatusCode);

            await using var owner = await OwnerAsync();
            Assert.Null(await owner.QuerySingleAsync<Guid?>("select account_id from players where tenant_id = @tenant and id = @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
            Assert.Equal(1L, await owner.QuerySingleAsync<long>("select count(*) from auth_sessions where tenant_id = @tenant and guest_id = @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Occupied_account_returns_address_taken_without_changing_the_guest()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        var account = await CreateTenantAccountAsync(seeded.TenantId, "taken-route@example.test");
        await InsertCodeAsync("taken-route@example.test", "123456");
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = LocalAddress(app) };
        try
        {
            var context = new DefaultHttpContext();
            await app.Services.GetRequiredService<IPlayerSessionIssuer>().IssueGuestAsync(context, seeded.TenantId, seeded.GuestId);
            var cookie = CookieValue(context);
            var csrf = await AntiforgeryAsync(client, seeded.Host, cookie);

            using (var refused = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/me/upgrade", cookie, csrf, seeded.Origin,
                       JsonContent.Create(new { email = "taken-route@example.test", code = "123456" })))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Equal("address_taken", (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
            }
            using (var stillGuest = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", cookie, null, null))
            {
                Assert.Equal(HttpStatusCode.OK, stillGuest.StatusCode);
                Assert.Equal(seeded.GuestId, (await stillGuest.Content.ReadFromJsonAsync<ProfileResponse>())!.Id);
            }

            await using var owner = await OwnerAsync();
            Assert.Null(await owner.QuerySingleAsync<Guid?>("select account_id from players where tenant_id = @tenant and id = @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
            Assert.Equal(account, await owner.QuerySingleAsync<Guid>("select account_id from players where tenant_id = @tenant and id <> @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
            Assert.Equal(1L, await owner.QuerySingleAsync<long>("select count(*) from auth_sessions where tenant_id = @tenant and guest_id = @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Attached_guest_with_a_surviving_cookie_recovers_session_rotation_without_reusing_a_code()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        var accountId = Guid.NewGuid();
        await using var owner = await OwnerAsync();
        await owner.ExecuteAsync("insert into accounts (id, email) values (@accountId, 'recovery@example.test'); update players set account_id = @accountId where tenant_id = @tenantId and id = @guestId", new { accountId, tenantId = seeded.TenantId, guestId = seeded.GuestId });
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = LocalAddress(app) };
        try
        {
            var firstContext = new DefaultHttpContext();
            var secondContext = new DefaultHttpContext();
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            await sessions.IssueGuestAsync(firstContext, seeded.TenantId, seeded.GuestId);
            await sessions.IssueGuestAsync(secondContext, seeded.TenantId, seeded.GuestId);
            var firstCookie = CookieValue(firstContext);
            var secondCookie = CookieValue(secondContext);
            var csrf = await AntiforgeryAsync(client, seeded.Host, firstCookie);

            using var recovered = await SendAsync(client, seeded.Host, HttpMethod.Post, "/api/me/upgrade", firstCookie, csrf, seeded.Origin,
                JsonContent.Create(new { email = "recovery@example.test", code = "already-consumed" }));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var accountCookie = CookieValue(recovered);

            using (var oldSecond = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", secondCookie, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, oldSecond.StatusCode);
            using (var account = await SendAsync(client, seeded.Host, HttpMethod.Get, "/api/me", accountCookie, null, null))
                Assert.Equal(HttpStatusCode.OK, account.StatusCode);
            Assert.Equal(0L, await owner.QuerySingleAsync<long>("select count(*) from auth_sessions where tenant_id = @tenant and guest_id = @guest", new { tenant = seeded.TenantId, guest = seeded.GuestId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private async Task<SeededTenant> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenant = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var host = $"guest-upgrade-{tenant:N}.example.test";
        await using var owner = await OwnerAsync();
        await owner.ExecuteAsync("delete from auth_sessions; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from data_protection_keys; delete from session_participants; delete from sessions; delete from tenants; delete from accounts");
        await owner.ExecuteAsync("""
            insert into tenants (id, slug, name, default_language) values (@tenant, @slug, 'Guest upgrade', 'en');
            insert into tenant_hosts (tenant_id, host) values (@tenant, @host);
            insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English');
            insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @guest, 'Guest', 'seed', 'en');
            """, new { tenant, guest, host, slug = $"guest-upgrade-{tenant:N}" });
        return new(tenant, guest, host);
    }

    private async Task InsertCodeAsync(string email, string code, DateTimeOffset? expiresAt = null)
    {
        await using var owner = await OwnerAsync();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("platform-test-email-auth-secret"), Encoding.UTF8.GetBytes(code));
        await owner.ExecuteAsync("insert into login_codes (email, code_hash, expires_at) values (@email, @hash, @expiresAt)", new { email, hash, expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10) });
    }

    private async Task<Guid> CreateTenantAccountAsync(Guid tenant, string email)
    {
        var account = Guid.NewGuid();
        await using var owner = await OwnerAsync();
        await owner.ExecuteAsync("""
            insert into accounts (id, email) values (@account, @email);
            insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenant, @player, @account, 'Account', 'seed', 'en');
            """, new { tenant, account, player = Guid.NewGuid(), email });
        return account;
    }

    private static int NextSiloPort() => Interlocked.Increment(ref nextSiloPort);

    private static Uri LocalAddress(WebApplication app) => new(app.Urls.Single());

    private async Task<WebApplication> StartAsync()
    {
        var siloPort = NextSiloPort();
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var app = Program.CreateApplication(sources, siloPort, siloPort + 20000);
        app.Urls.Add("http://127.0.0.1:0");
        try
        {
            await app.StartAsync();
            await app.Services.GetRequiredService<PostgresClock>().StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, string host, string cookie)
    {
        using var response = await SendAsync(client, host, HttpMethod.Get, "/api/antiforgery", cookie, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string host, HttpMethod method, string path, string? cookie, string? csrf, string? origin, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qp={cookie}");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF", csrf);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static string CookieValue(DefaultHttpContext context) => CookieValue(context.Response.Headers.SetCookie);
    private static string CookieValue(HttpResponseMessage response) => CookieValue(response.Headers.GetValues("Set-Cookie"));
    private static string CookieValue(IEnumerable<string> headers)
    {
        var header = Assert.Single(headers);
        var value = header["__Host-qp=".Length..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private sealed record SeededTenant(Guid TenantId, Guid GuestId, string Host)
    {
        public string Origin => $"http://{Host}";
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record StatusResponse(string Status);
    private sealed record ErrorResponse(string Error);
    private sealed record ProfileResponse(Guid Id, string DisplayName, string AvatarSeed, string Lang);

    private sealed class GuestUpgradeHostFeature : IFeature
    {
        public string Id => "guest_upgrade_host";

        public FeatureSettings Settings => FeatureSettings.Create(new GuestUpgradeHostSettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
        }
    }

    private sealed record GuestUpgradeHostSettings;
}
