using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Infrastructure.Clock;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Security;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminAuthDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("5c17c8d3-a33c-4a82-a10f-b216262a0449");
    private static readonly Guid TenantB = Guid.Parse("1d0fa565-2d0c-4949-a4b5-1370d5d78e45");

    [SkippableFact]
    public async Task Login_requires_an_active_tenant_admin_and_locks_after_ten_failures()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var hasher = new IdentityPasswordHasher();
        var service = new AdminAuthService(sources, hasher, new CapturingMailer(), clock);
        var admin = await InsertAdminAsync(owner, hasher, "tenant-admin", "admin@example.test", "A strong password 2026!", mustChangePassword: true);
        await ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id) values (@tenant, @admin)", ("tenant", TenantA), ("admin", admin));

        Assert.Equal(AdminLoginStatus.InvalidCredentials, (await service.LoginAsync(TenantB, "tenant-admin", "A strong password 2026!")).Status);
        for (var attempt = 0; attempt < 9; attempt++)
            Assert.Equal(AdminLoginStatus.InvalidCredentials, (await service.LoginAsync(TenantA, "tenant-admin", "incorrect password")).Status);

        Assert.Equal(AdminLoginStatus.Locked, (await service.LoginAsync(TenantA, "tenant-admin", "incorrect password")).Status);
        Assert.Equal(AdminLoginStatus.InvalidCredentials, (await service.LoginAsync(TenantB, "tenant-admin", "A strong password 2026!")).Status);
        Assert.Equal((short)10, await ScalarAsync<short>(owner, "select failed_attempts from admins where id = @admin", ("admin", admin)));
        Assert.Equal(AdminLoginStatus.Locked, (await service.LoginAsync(TenantA, "tenant-admin", "A strong password 2026!")).Status);

        await ExecuteAsync(owner, "update admins set locked_until = @until where id = @admin", ("admin", admin), ("until", clock.Now.UtcDateTime.AddSeconds(-1)));
        var signedIn = await service.LoginAsync(TenantA, "tenant-admin", "A strong password 2026!");
        Assert.Equal(AdminLoginStatus.Success, signedIn.Status);
        Assert.Equal(admin, signedIn.AdminId);
        Assert.True(signedIn.MustChangePassword);
        Assert.True(await ScalarAsync<bool>(owner, "select last_login_at is not null and failed_attempts = 0 from admins where id = @admin", ("admin", admin)));

        var platformAdmin = await InsertAdminAsync(owner, hasher, "platform-admin", "platform@example.test", "Platform password 2026!", isPlatform: true);
        Assert.Equal(AdminLoginStatus.Success, (await service.LoginAsync(TenantB, "platform-admin", "Platform password 2026!")).Status);

        Assert.Equal(AdminPasswordChangeStatus.WrongCurrentPassword, (await service.ChangePasswordAsync(admin, "wrong password", "Updated password 2026!")).Status);
        Assert.Equal(AdminPasswordChangeStatus.WeakPassword, (await service.ChangePasswordAsync(admin, "A strong password 2026!", "short")).Status);
        Assert.True(await hasher.VerifyAsync(owner, admin, "A strong password 2026!"));
        Assert.Equal(AdminPasswordChangeStatus.Success, (await service.ChangePasswordAsync(admin, "A strong password 2026!", "Updated password 2026!")).Status);
        Assert.True(await ScalarAsync<bool>(owner, "select not must_change_password from admins where id = @admin", ("admin", admin)));
    }

    [SkippableFact]
    public async Task Unknown_and_inactive_logins_burn_a_password_hash_without_disclosing_the_account()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var hasher = new CountingHasher();
        var service = new AdminAuthService(sources, hasher, new CapturingMailer(), new TestClock(DateTimeOffset.UtcNow));
        await InsertAdminAsync(owner, hasher, "inactive-admin", "inactive@example.test", "A strong password 2026!", isActive: false);

        var unknown = await service.LoginAsync(TenantA, "missing-admin", "anything");
        var inactive = await service.LoginAsync(TenantA, "inactive-admin", "anything");
        var active = await InsertAdminAsync(owner, hasher, "active-admin", "active@example.test", "anything");
        await ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id) values (@tenant, @admin)", ("tenant", TenantA), ("admin", active));
        var wrongPassword = await service.LoginAsync(TenantA, "active-admin", "anything");

        Assert.Equal(AdminLoginStatus.InvalidCredentials, unknown.Status);
        Assert.Equal(AdminLoginStatus.InvalidCredentials, inactive.Status);
        Assert.Equal(AdminLoginStatus.InvalidCredentials, wrongPassword.Status);
        Assert.Equal(2, hasher.Burns);
        Assert.Equal(1, hasher.Verifications);
    }

    [SkippableFact]
    public async Task Login_limits_unknown_usernames_per_client_address_before_password_work()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var hasher = new CountingHasher();
        var service = new AdminAuthService(sources, hasher, new CapturingMailer(), new TestClock(DateTimeOffset.UtcNow));

        for (var attempt = 0; attempt < 20; attempt++)
            Assert.Equal(AdminLoginStatus.InvalidCredentials, (await service.LoginAsync(TenantA, $"missing-{attempt}", "anything", "192.0.2.1")).Status);

        Assert.Equal(AdminLoginStatus.InvalidCredentials, (await service.LoginAsync(TenantA, "missing-limited", "anything", "192.0.2.1")).Status);
        Assert.Equal(20, hasher.Burns);
        Assert.Equal(21, await ScalarAsync<int>(owner, "select count from auth_rate_limits where key = @key", ("key", "admin-ip:192.0.2.1")));
    }

    [SkippableFact]
    public async Task Password_resets_are_hashed_and_consumed_once_after_a_valid_new_password()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var hasher = new IdentityPasswordHasher();
        var mailer = new CapturingMailer();
        var service = new AdminAuthService(sources, hasher, mailer, new TestClock(DateTimeOffset.UtcNow));
        var admin = await InsertAdminAsync(owner, hasher, "reset-admin", "reset@example.test", "A strong password 2026!");

        await service.RequestResetAsync("reset@example.test", "https://tenant-a.example/admin/reset");
        var token = TokenFrom(mailer.Links.Single());
        Assert.Equal(32, (await ScalarAsync<byte[]>(owner, "select token_hash from admin_password_resets where admin_id = @admin", ("admin", admin))).Length);

        var weak = await service.ResetAsync(token, "short");
        Assert.Equal(AdminPasswordChangeStatus.WeakPassword, weak.Status);
        Assert.False(await ScalarAsync<bool>(owner, "select used_at is not null from admin_password_resets where admin_id = @admin", ("admin", admin)));

        var resets = await Task.WhenAll(
            service.ResetAsync(token, "New strong password 2026!"),
            service.ResetAsync(token, "New strong password 2026!"));
        Assert.Single(resets, result => result.Status == AdminPasswordChangeStatus.Success);
        Assert.Single(resets, result => result.Status == AdminPasswordChangeStatus.UsedToken);
        Assert.True(await hasher.VerifyAsync(owner, admin, "New strong password 2026!"));

        await service.RequestResetAsync("reset-admin", "https://tenant-a.example/admin/reset");
        var expiredToken = TokenFrom(mailer.Links.Last());
        await ExecuteAsync(owner, "update admin_password_resets set expires_at = now() - interval '1 second' where token_hash = @hash", ("hash", HashToken(expiredToken)));
        Assert.Equal(AdminPasswordChangeStatus.ExpiredToken, (await service.ResetAsync(expiredToken, "Another strong password 2026!")).Status);

        await InsertAdminAsync(owner, hasher, "reset-handle", "winner@example.test", "A strong password 2026!");
        await InsertAdminAsync(owner, hasher, "other-admin", "reset-handle", "A strong password 2026!");
        await service.RequestResetAsync("reset-handle", "https://tenant-a.example/admin/reset");
        Assert.Equal("reset-handle", mailer.Usernames.Last());
        Assert.Equal("winner@example.test", mailer.Emails.Last());
    }

    [SkippableFact]
    public async Task Admin_sessions_are_tenant_bound_and_cannot_be_player_sessions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var hasher = new IdentityPasswordHasher();
        var admin = await InsertAdminAsync(owner, hasher, "session-admin", "session@example.test", "A strong password 2026!");
        await ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id) values (@tenant, @admin)", ("tenant", TenantA), ("admin", admin));
        var sessions = new AdminSessionService(sources, new TestClock(DateTimeOffset.UtcNow));
        var context = new DefaultHttpContext();

        Assert.True(await sessions.IssueAsync(context, TenantA, admin, mustChangePassword: false));
        var header = Assert.Single(context.Response.Headers.SetCookie)!;
        Assert.Contains("__Host-qa=", header, StringComparison.Ordinal);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        var token = Uri.UnescapeDataString(header[(header.IndexOf('=') + 1)..header.IndexOf(';')]);

        Assert.Equal(admin, (await sessions.ResolveAsync(TenantA, token))?.AdminId);
        Assert.Null(await sessions.ResolveAsync(TenantB, token));
        var players = new PlayerSessionService(sources, new TenantDb(sources), new TestClock(DateTimeOffset.UtcNow));
        Assert.Null(await players.ResolveAsync(TenantA, token));
        var playerToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var account = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, @email); insert into auth_sessions (token_hash, tenant_id, account_id, expires_at) values (@hash, @tenant, @account, now() + interval '1 hour')", ("account", account), ("email", "player@example.test"), ("hash", HashToken(playerToken)), ("tenant", TenantA));
        Assert.Null(await sessions.ResolveAsync(TenantA, playerToken));
        await sessions.RevokeAsync(token);
        Assert.Null(await sessions.ResolveAsync(TenantA, token));
    }

    [SkippableFact]
    public async Task Inactive_admin_sessions_cannot_resolve_or_access_admin_routes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var hasher = new IdentityPasswordHasher();
        var admin = await InsertAdminAsync(owner, hasher, "deactivated-admin", "deactivated@example.test", "A strong password 2026!");
        await ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id) values (@tenant, @admin)", ("tenant", TenantA), ("admin", admin));
        var sessions = new AdminSessionService(sources, new TestClock(DateTimeOffset.UtcNow));
        var context = new DefaultHttpContext();
        Assert.True(await sessions.IssueAsync(context, TenantA, admin, mustChangePassword: false));
        var token = CookieValue(context.Response.Headers.SetCookie, "__Host-qa");
        await ExecuteAsync(owner, "update admins set is_active = false where id = @admin", ("admin", admin));

        Assert.Null(await sessions.ResolveAsync(TenantA, token));

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50149") };
        var host = "admin-auth-a.example.test";
        try
        {
            using var response = await SendAsync(client, host, HttpMethod.Post, "/api/admin/auth/password", token, null, $"http://{host}",
                JsonContent.Create(new { currentPassword = "A strong password 2026!", newPassword = "An updated strong password 2026!" }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Login_and_sign_out_use_the_separate_admin_cookie_and_antiforgery_contract()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var hasher = new IdentityPasswordHasher();
        var admin = await InsertAdminAsync(owner, hasher, "api-admin", "api-admin@example.test", "A strong password 2026!", mustChangePassword: true);
        await ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id) values (@tenant, @admin)", ("tenant", TenantA), ("admin", admin));
        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50149") };
        var host = "admin-auth-a.example.test";
        var origin = $"http://{host}";
        try
        {
            var anonymousCsrf = await AdminAntiforgeryAsync(client, host, null);
            using var login = await SendAsync(client, host, HttpMethod.Post, "/api/admin/auth/login", null, anonymousCsrf, origin,
                JsonContent.Create(new { username = "api-admin", password = "A strong password 2026!" }));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.True((await login.Content.ReadFromJsonAsync<LoginResponse>())!.MustChangePassword);
            var cookie = CookieValue(login, "__Host-qa");
            Assert.Contains("path=/", login.Headers.GetValues("Set-Cookie").Single(header => header.StartsWith("__Host-qa=", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
            var otherSession = new DefaultHttpContext();
            var sessions = new AdminSessionService(sources, new TestClock(DateTimeOffset.UtcNow));
            Assert.True(await sessions.IssueAsync(otherSession, TenantA, admin, mustChangePassword: true));
            var otherToken = CookieValue(otherSession.Response.Headers.SetCookie, "__Host-qa");

            using (var player = await SendAsync(client, host, HttpMethod.Get, "/api/me", cookie, null, null, cookieName: "__Host-qa"))
                Assert.Equal(HttpStatusCode.Unauthorized, player.StatusCode);

            var adminCsrf = await AdminAntiforgeryAsync(client, host, cookie);
            using (var password = await SendAsync(client, host, HttpMethod.Post, "/api/admin/auth/password", cookie, adminCsrf, origin,
                       JsonContent.Create(new { currentPassword = "A strong password 2026!", newPassword = "An updated strong password 2026!" })))
            {
                Assert.Equal(HttpStatusCode.OK, password.StatusCode);
                Assert.False((await password.Content.ReadFromJsonAsync<LoginResponse>())!.MustChangePassword);
            }

            using var signOut = await SendAsync(client, host, HttpMethod.Post, "/api/admin/auth/sign-out", cookie, adminCsrf, origin);
            Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
            var clearedCookie = signOut.Headers.GetValues("Set-Cookie").Single(header => header.StartsWith("__Host-qa=", StringComparison.Ordinal));
            Assert.StartsWith("__Host-qa=;", clearedCookie, StringComparison.Ordinal);
            Assert.Contains("path=/", clearedCookie, StringComparison.OrdinalIgnoreCase);
            Assert.True(clearedCookie.Contains("expires=", StringComparison.OrdinalIgnoreCase) || clearedCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from auth_sessions where admin_id = @admin", ("admin", admin)));
            Assert.Equal(admin, (await sessions.ResolveAsync(TenantA, otherToken))?.AdminId);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null) return;
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from auth_sessions; delete from admin_password_resets; delete from admin_memberships; delete from admins; delete from tenant_languages; delete from tenants; delete from accounts");
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from auth_sessions; delete from admin_password_resets; delete from admin_memberships; delete from admins; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from data_protection_keys; delete from session_participants; delete from sessions; delete from tenant_languages; delete from tenants; delete from accounts");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'admin-auth-a', 'Admin auth A', 'en'), (@b, 'admin-auth-b', 'Admin auth B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_hosts (tenant_id, host) values (@a, 'admin-auth-a.example.test'), (@b, 'admin-auth-b.example.test')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private static async Task<Guid> InsertAdminAsync(NpgsqlConnection owner, IAdminPasswordHasher hasher, string username, string email, string password, bool isActive = true, bool mustChangePassword = false, bool isPlatform = false)
    {
        var admin = Guid.NewGuid();
        await ExecuteAsync(owner, """
            insert into admins (id, username, email, password_hash, is_active, must_change_password, is_platform)
            values (@id, @username, @email, @passwordHash, @isActive, @mustChangePassword, @isPlatform)
            """, ("id", admin), ("username", username), ("email", email), ("passwordHash", hasher.Hash(password)), ("isActive", isActive), ("mustChangePassword", mustChangePassword), ("isPlatform", isPlatform));
        return admin;
    }

    private static byte[] HashToken(string token) => System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(token));

    private static string TokenFrom(string link)
    {
        var token = new Uri(link).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith("token=", StringComparison.Ordinal))[6..];
        return Uri.UnescapeDataString(token);
    }

    private async Task<WebApplication> StartAsync()
    {
        var app = Program.CreateApplication(Sources(), 11149, 30149);
        app.Urls.Add("http://127.0.0.1:50149");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        return app;
    }

    private static async Task<string> AdminAntiforgeryAsync(HttpClient client, string host, string? cookie)
    {
        using var response = await SendAsync(client, host, HttpMethod.Get, "/api/admin/antiforgery", cookie, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string host, HttpMethod method, string path, string? cookie, string? csrf, string? origin, HttpContent? content = null, string cookieName = "__Host-qa")
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={cookie}");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF", csrf);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static string CookieValue(HttpResponseMessage response, string name) => CookieValue(response.Headers.GetValues("Set-Cookie"), name);

    private static string CookieValue(IEnumerable<string> headers, string name)
    {
        var header = Assert.Single(headers, value => value.StartsWith($"{name}=", StringComparison.Ordinal));
        var value = header[(name.Length + 1)..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record LoginResponse(bool MustChangePassword);

    private sealed class CapturingMailer : IAdminPasswordResetMailer
    {
        public ConcurrentQueue<string> Links { get; } = [];
        public ConcurrentQueue<string> Usernames { get; } = [];
        public ConcurrentQueue<string> Emails { get; } = [];

        public Task SendAsync(string email, string username, string resetLink, CancellationToken cancellationToken = default)
        {
            Links.Enqueue(resetLink);
            Usernames.Enqueue(username);
            Emails.Enqueue(email);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingHasher : IAdminPasswordHasher
    {
        public int Burns { get; private set; }
        public int Verifications { get; private set; }

        public string Hash(string password) => password;
        public bool Verify(string hash, string password)
        {
            Verifications++;
            return false;
        }

        public void BurnTime() => Burns++;
    }
}

file static class AdminPasswordHashAssertions
{
    public static async Task<bool> VerifyAsync(this IAdminPasswordHasher hasher, NpgsqlConnection owner, Guid admin, string password)
    {
        await using var command = new NpgsqlCommand("select password_hash from admins where id = @admin", owner);
        command.Parameters.AddWithValue("admin", admin);
        return hasher.Verify((string)(await command.ExecuteScalarAsync())!, password);
    }
}
