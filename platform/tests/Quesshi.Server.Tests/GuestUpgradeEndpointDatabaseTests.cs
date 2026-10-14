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
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class GuestUpgradeEndpointDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Upgrade_rotates_every_guest_session_and_keeps_the_same_player()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await InsertCodeAsync("upgrade-route@example.test", "123456");
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50148") };
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
        await InsertCodeAsync("failed-route@example.test", "123456");
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50148") };
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

    private async Task<SeededTenant> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenant = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var host = $"guest-upgrade-{tenant:N}.example.test";
        await using var owner = await OwnerAsync();
        await owner.ExecuteAsync("delete from auth_sessions; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from data_protection_keys; delete from tenants; delete from accounts");
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

    private async Task<WebApplication> StartAsync()
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var app = Program.CreateApplication(sources, 11148, 30148);
        app.Urls.Add("http://127.0.0.1:50148");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        return app;
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
    private sealed record ProfileResponse(Guid Id, string DisplayName, string AvatarSeed, string Lang);
}
