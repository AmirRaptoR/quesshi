using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    private const string OtherTenantHost = "security-other.example.test";

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
    public async Task Stale_admin_antiforgery_returns_the_client_retry_error()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11180, 30180, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var cookie = await IssueAdminCookieAsync(sessions, seeded.TenantId, seeded.ModeratorAdminId);
            using var response = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", cookie, "stale", TenantOrigin);
            var actual = new StaleAntiforgeryResponse(response.StatusCode, (await response.Content.ReadFromJsonAsync<BannedError>())?.Error);

            Assert.Equal(new StaleAntiforgeryResponse(HttpStatusCode.BadRequest, "invalid_antiforgery"), actual);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Stale_admin_auth_antiforgery_returns_the_client_retry_error()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11181, 30181, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await SendAdminAsync(
                client,
                TenantHost,
                HttpMethod.Post,
                "/api/admin/auth/login",
                null,
                "stale",
                TenantOrigin,
                JsonContent.Create(new { username = "missing", password = "A strong password 2026!" }));
            var actual = new StaleAntiforgeryResponse(response.StatusCode, (await response.Content.ReadFromJsonAsync<BannedError>())?.Error);

            Assert.Equal(new StaleAntiforgeryResponse(HttpStatusCode.BadRequest, "invalid_antiforgery"), actual);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Moderator_ban_and_unban_are_authorized_audited_and_tenant_scoped()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        await using var app = Program.CreateApplication(sources, 11174, 30174);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var moderatorCookie = await IssueAdminCookieAsync(sessions, seeded.TenantId, seeded.ModeratorAdminId);
            var editorCookie = await IssueAdminCookieAsync(sessions, seeded.TenantId, seeded.EditorAdminId);
            var forcedCookie = await IssueAdminCookieAsync(sessions, seeded.TenantId, seeded.ForcedAdminId, mustChangePassword: true);

            using (var anonymous = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.PlayerId}/ban", null, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var editor = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.PlayerId}/ban", editorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, editorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NoContent, editor.StatusCode);
            using (var forced = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.PlayerId}/ban", forcedCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, forcedCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.Forbidden, forced.StatusCode);
            using (var foreignPlayer = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.OtherPlayerId}/ban", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NotFound, foreignPlayer.StatusCode);

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.True(await owner.ExecuteScalarAsync<bool>("select banned_at is not null from players where tenant_id = @tenantId and id = @playerId", new { tenantId = seeded.TenantId, playerId = seeded.PlayerId }));

            using (var unbanned = await SendAdminAsync(client, TenantHost, HttpMethod.Delete, $"/api/admin/players/{seeded.PlayerId}/ban", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NoContent, unbanned.StatusCode);

            Assert.False(await owner.ExecuteScalarAsync<bool>("select banned_at is not null from players where tenant_id = @tenantId and id = @playerId", new { tenantId = seeded.TenantId, playerId = seeded.PlayerId }));
            Assert.False(await owner.ExecuteScalarAsync<bool>("select banned_at is not null from players where tenant_id = @tenantId and id = @playerId", new { tenantId = seeded.OtherTenantId, playerId = seeded.OtherPlayerId }));
            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from audit_log where tenant_id = @tenantId and actor_id = @actorId and action = 'admin.player.ban' and target_type = 'player' and target_id = @playerId", new { tenantId = seeded.TenantId, actorId = seeded.EditorAdminId, playerId = seeded.PlayerId }));
            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from audit_log where tenant_id = @tenantId and actor_id = @actorId and action = 'admin.player.unban' and target_type = 'player' and target_id = @playerId", new { tenantId = seeded.TenantId, actorId = seeded.ModeratorAdminId, playerId = seeded.PlayerId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Banning_an_account_player_revokes_its_tenant_session_and_rejects_core_and_plugin_routes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11175, 30175, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:50175");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50175") };
            var playerSessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var adminSessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var accountContext = new DefaultHttpContext();
            await playerSessions.IssueAccountAsync(accountContext, seeded.TenantId, seeded.AccountId);
            var accountCookie = CookieValue(accountContext);
            var accountToken = await AntiforgeryAsync(client, accountCookie);
            var otherAccountContext = new DefaultHttpContext();
            await playerSessions.IssueAccountAsync(otherAccountContext, seeded.OtherTenantId, seeded.AccountId);
            var otherAccountCookie = CookieValue(otherAccountContext);
            var otherAccountToken = await AntiforgeryAsync(client, otherAccountCookie, OtherTenantHost);
            var moderatorCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.ModeratorAdminId);

            using (var ban = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.PlayerId}/ban", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NoContent, ban.StatusCode);
            using (var core = await SendAsync(client, HttpMethod.Get, "/api/me", accountCookie, null))
                await AssertBannedAsync(core);
            using (var plugin = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", accountCookie, accountToken, TenantOrigin))
                await AssertBannedAsync(plugin);
            using (var otherTenant = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", otherAccountCookie, otherAccountToken, OtherTenantOrigin, host: OtherTenantHost))
                Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from auth_sessions where tenant_id = @tenantId and account_id = @accountId and revoked_at is not null", new { tenantId = seeded.TenantId, accountId = seeded.AccountId }));
            Assert.Equal(0L, await owner.ExecuteScalarAsync<long>("select count(*) from auth_sessions where tenant_id = @tenantId and account_id = @accountId and revoked_at is not null", new { tenantId = seeded.OtherTenantId, accountId = seeded.AccountId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Banning_a_guest_player_revokes_only_its_current_tenant_session()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        await using var app = Program.CreateApplication(sources, 11176, 30176);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var playerSessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var adminSessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var guestContext = new DefaultHttpContext();
            await playerSessions.IssueGuestAsync(guestContext, seeded.TenantId, seeded.GuestId);
            var guestCookie = CookieValue(guestContext);
            var otherGuestContext = new DefaultHttpContext();
            await playerSessions.IssueGuestAsync(otherGuestContext, seeded.OtherTenantId, seeded.OtherGuestId);
            var otherGuestCookie = CookieValue(otherGuestContext);
            var moderatorCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.ModeratorAdminId);

            using (var ban = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.GuestId}/ban", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NoContent, ban.StatusCode);
            using (var revoked = await SendAsync(client, HttpMethod.Get, "/api/me", guestCookie, null))
                await AssertBannedAsync(revoked);
            using (var otherTenant = await SendAsync(client, HttpMethod.Get, "/api/me", otherGuestCookie, null, host: OtherTenantHost))
                Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from auth_sessions where tenant_id = @tenantId and guest_id = @playerId and revoked_at is not null", new { tenantId = seeded.TenantId, playerId = seeded.GuestId }));
            Assert.Equal(0L, await owner.ExecuteScalarAsync<long>("select count(*) from auth_sessions where tenant_id = @tenantId and guest_id = @playerId and revoked_at is not null", new { tenantId = seeded.OtherTenantId, playerId = seeded.OtherGuestId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task A_deleted_player_session_is_refused_on_core_and_plugin_routes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11178, 30178, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var context = new DefaultHttpContext();
            await sessions.IssueAccountAsync(context, seeded.TenantId, seeded.AccountId);
            var cookie = CookieValue(context);
            var token = await AntiforgeryAsync(client, cookie);

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            await owner.ExecuteAsync("update players set deleted_at = now() where tenant_id = @tenantId and id = @playerId",
                new { tenantId = seeded.TenantId, playerId = seeded.PlayerId });

            using (var core = await SendAsync(client, HttpMethod.Get, "/api/me", cookie, null))
                await AssertDeletedIdentityAsync(core);
            using (var plugin = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", cookie, token, TenantOrigin))
                await AssertDeletedIdentityAsync(plugin);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task A_deleted_account_session_is_refused_on_core_and_plugin_routes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11179, 30179, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:50179");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50179") };
            var sessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var context = new DefaultHttpContext();
            await sessions.IssueAccountAsync(context, seeded.TenantId, seeded.AccountId);
            var cookie = CookieValue(context);
            var token = await AntiforgeryAsync(client, cookie);

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            await owner.ExecuteAsync("update accounts set deleted_at = now(), email = null where id = @accountId",
                new { accountId = seeded.AccountId });

            using (var core = await SendAsync(client, HttpMethod.Get, "/api/me", cookie, null))
                await AssertDeletedIdentityAsync(core);
            using (var plugin = await SendAsync(client, HttpMethod.Post, "/api/x/security/player/json", cookie, token, TenantOrigin))
                await AssertDeletedIdentityAsync(plugin);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Banned_email_verification_is_indistinguishable_from_a_wrong_code_and_unban_allows_sign_in()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        await using var app = Program.CreateApplication(sources, 11177, 30177);
        app.Urls.Add("http://127.0.0.1:50177");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50177") };
            var adminSessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var moderatorCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.ModeratorAdminId);
            using (var ban = await SendAdminAsync(client, TenantHost, HttpMethod.Post, $"/api/admin/players/{seeded.PlayerId}/ban", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NoContent, ban.StatusCode);

            await InsertLoginCodeAsync(seeded.Email, "123456");
            using var banned = await SendAsync(client, HttpMethod.Post, "/api/auth/verify", null, await AntiforgeryAsync(client, null), TenantOrigin,
                JsonContent.Create(new { email = seeded.Email, code = "123456" }));
            using var wrong = await SendAsync(client, HttpMethod.Post, "/api/auth/verify", null, await AntiforgeryAsync(client, null), TenantOrigin,
                JsonContent.Create(new { email = seeded.Email, code = "000000" }));
            Assert.Equal(HttpStatusCode.Unauthorized, banned.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(await wrong.Content.ReadAsStringAsync(), await banned.Content.ReadAsStringAsync());

            using (var unban = await SendAdminAsync(client, TenantHost, HttpMethod.Delete, $"/api/admin/players/{seeded.PlayerId}/ban", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.NoContent, unban.StatusCode);
            await InsertLoginCodeAsync(seeded.Email, "234567");
            using var signedIn = await SendAsync(client, HttpMethod.Post, "/api/auth/verify", null, await AntiforgeryAsync(client, null), TenantOrigin,
                JsonContent.Create(new { email = seeded.Email, code = "234567" }));
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-qp=", StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Account_deletion_requires_an_account_session_clears_its_cookie_and_rejects_it_afterward()
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
                Assert.Contains(deleted.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-qp=", StringComparison.Ordinal) && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
            }
            using var oldCookie = await SendAsync(client, HttpMethod.Get, "/api/me", accountCookie, null, null);
            Assert.Equal(HttpStatusCode.Unauthorized, oldCookie.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Admin_routes_enforce_roles_antiforgery_and_tenant_scoped_auditing()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11152, 30152, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var adminSessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var playerSessions = app.Services.GetRequiredService<IPlayerSessionIssuer>();
            var ownerCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.OwnerAdminId);
            var editorCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.EditorAdminId);
            var moderatorCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.ModeratorAdminId);
            var platformCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.PlatformAdminId);
            var forcedCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.ForcedAdminId, mustChangePassword: true);
            var ownerToken = await AdminAntiforgeryAsync(client, TenantHost, ownerCookie);
            var playerContext = new DefaultHttpContext();
            await playerSessions.IssueAccountAsync(playerContext, seeded.TenantId, seeded.AccountId);
            var playerCookie = CookieValue(playerContext, "__Host-qp");
            var playerToken = await AntiforgeryAsync(client, playerCookie);

            using (var anonymous = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", null, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var player = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", playerCookie, null, null, cookieName: "__Host-qp"))
                Assert.Equal(HttpStatusCode.Unauthorized, player.StatusCode);
            using (var editor = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", editorCookie, null, null))
                Assert.Equal(HttpStatusCode.Forbidden, editor.StatusCode);
            using (var moderator = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/editor", moderatorCookie, ownerToken, TenantOrigin))
                Assert.Equal(HttpStatusCode.Forbidden, moderator.StatusCode);
            using (var moderatorRoute = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", moderatorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, moderatorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.OK, moderatorRoute.StatusCode);
            using (var editorRoute = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", editorCookie,
                       await AdminAntiforgeryAsync(client, TenantHost, editorCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.OK, editorRoute.StatusCode);
            using (var forced = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", forcedCookie, ownerToken, TenantOrigin))
                Assert.Equal(HttpStatusCode.Forbidden, forced.StatusCode);
            using (var foreignOrigin = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", ownerCookie, ownerToken, "https://foreign.example.test"))
                Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
            using (var missingOrigin = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", ownerCookie, ownerToken, null))
                Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);
            using (var playerTokenDenied = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", ownerCookie, playerToken, TenantOrigin, csrfHeader: "X-CSRF"))
                Assert.Equal(HttpStatusCode.BadRequest, playerTokenDenied.StatusCode);
            using (var json = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", ownerCookie, ownerToken, TenantOrigin, JsonContent.Create(new { value = "moderator" })))
                Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            using (var multipart = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator-multipart", ownerCookie, ownerToken, TenantOrigin,
                       new MultipartFormDataContent { { new StringContent("file"), "upload" } }))
                Assert.Equal(HttpStatusCode.OK, multipart.StatusCode);
            using (var platform = await SendAdminAsync(client, TenantHost, HttpMethod.Post, "/api/admin/x/security/owner", platformCookie, await AdminAntiforgeryAsync(client, TenantHost, platformCookie), TenantOrigin))
                Assert.Equal(HttpStatusCode.OK, platform.StatusCode);

            var otherPlatformCookie = await IssueAdminCookieAsync(adminSessions, seeded.OtherTenantId, seeded.OtherPlatformAdminId);
            using (var otherTenant = await SendAdminAsync(client, OtherTenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", otherPlatformCookie,
                       await AdminAntiforgeryAsync(client, OtherTenantHost, otherPlatformCookie), OtherTenantOrigin))
                Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);

            using var auditResponse = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit?take=1", ownerCookie, null, null);
            Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
            var auditPage = await auditResponse.Content.ReadFromJsonAsync<AuditPage>();
            Assert.NotNull(auditPage);
            Assert.NotNull(auditPage.Next);
            Assert.All(auditPage.Items, entry => Assert.NotEqual(seeded.OtherPlatformAdminId, entry.ActorId));

            using var nextAuditResponse = await SendAdminAsync(client, TenantHost, HttpMethod.Get, $"/api/admin/audit?before={auditPage.Next}&take=1", ownerCookie, null, null);
            Assert.Equal(HttpStatusCode.OK, nextAuditResponse.StatusCode);
            var nextAuditPage = await nextAuditResponse.Content.ReadFromJsonAsync<AuditPage>();
            Assert.NotNull(nextAuditPage);
            Assert.True(nextAuditPage.Items.Single().Id < auditPage.Items.Single().Id);

            using (var platformAudit = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", platformCookie, null, null))
                Assert.Equal(HttpStatusCode.OK, platformAudit.StatusCode);

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(10L, await owner.ExecuteScalarAsync<long>("select count(*) from audit_log where tenant_id = @tenantId", new { tenantId = seeded.TenantId }));
            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from audit_log where tenant_id = @tenantId", new { tenantId = seeded.OtherTenantId }));
            var auditRows = await owner.QueryAsync<AuditRow>("""
                select actor_id as ActorId, action as Action, target_type as TargetType, data::text as Data
                from audit_log where tenant_id = @tenantId order by id desc limit 1
                """, new { tenantId = seeded.TenantId });
            var audit = Assert.Single(auditRows);
            Assert.Equal(seeded.PlatformAdminId, audit.ActorId);
            Assert.Equal("admin.post", audit.Action);
            Assert.Equal("route", audit.TargetType);
            Assert.DoesNotContain(ownerCookie, audit.Data, StringComparison.Ordinal);

            await owner.ExecuteAsync("update auth_sessions set expires_at = now() - interval '1 minute' where admin_id = @adminId", new { adminId = seeded.EditorAdminId });
            using (var expired = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", editorCookie, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            await owner.ExecuteAsync("delete from admin_memberships where tenant_id = @tenantId and admin_id = @adminId", new { tenantId = seeded.TenantId, adminId = seeded.ModeratorAdminId });
            using (var noMembership = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", moderatorCookie, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, noMembership.StatusCode);
            await app.Services.GetRequiredService<IAdminSessionResolver>().RevokeAsync(ownerCookie);
            using (var revoked = await SendAdminAsync(client, TenantHost, HttpMethod.Get, "/api/admin/audit", ownerCookie, null, null))
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Admin_routes_enforce_security_before_typed_json_binding()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var app = Program.CreateApplication(sources, 11153, 30153, configuredFeatures: registry);
        app.Urls.Add("http://127.0.0.1:50153");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50153") };
            var adminSessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var ownerCookie = await IssueAdminCookieAsync(adminSessions, seeded.TenantId, seeded.OwnerAdminId);

            using (var knownAdmin = await SendAdminAsync(
                       client,
                       TenantHost,
                       HttpMethod.Post,
                       "/api/admin/x/security/moderator-typed",
                       ownerCookie,
                       "not-an-admin-token",
                       "https://foreign.example.test",
                       new StringContent("{", Encoding.UTF8, "application/json")))
            {
                Assert.Equal(HttpStatusCode.Forbidden, knownAdmin.StatusCode);
            }

            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from audit_log where tenant_id = @tenantId", new { tenantId = seeded.TenantId }));

            using (var anonymous = await SendAdminAsync(
                       client,
                       TenantHost,
                       HttpMethod.Post,
                       "/api/admin/x/security/moderator-typed",
                       null,
                       null,
                       null,
                       new StringContent("{", Encoding.UTF8, "application/json")))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from audit_log where tenant_id = @tenantId", new { tenantId = seeded.TenantId }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Admin_antiforgery_tokens_validate_on_another_silo()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var firstSources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        await using var secondSources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var firstRegistry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        var secondRegistry = FeatureRegistry.Create([new SecurityRoutesFeature()]);
        await using var first = Program.CreateApplication(firstSources, 11150, 30150, configuredFeatures: firstRegistry);
        await using var second = Program.CreateApplication(secondSources, 11151, 30151, configuredFeatures: secondRegistry);
        first.Urls.Add("http://127.0.0.1:0");
        second.Urls.Add("http://127.0.0.1:0");
        await first.StartAsync();
        await first.Services.GetRequiredService<PostgresClock>().StartAsync();
        await second.StartAsync();
        await second.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            var cookie = await IssueAdminCookieAsync(
                first.Services.GetRequiredService<IAdminSessionIssuer>(), seeded.TenantId, seeded.ModeratorAdminId);
            using var firstClient = new HttpClient { BaseAddress = new Uri(first.Urls.Single()) };
            var token = await AdminAntiforgeryAsync(firstClient, TenantHost, cookie);
            using var secondClient = new HttpClient { BaseAddress = new Uri(second.Urls.Single()) };
            using var response = await SendAdminAsync(secondClient, TenantHost, HttpMethod.Post, "/api/admin/x/security/moderator", cookie, token, TenantOrigin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await second.StopAsync();
            await first.StopAsync();
        }
    }

    private static string TenantOrigin => $"http://{TenantHost}";
    private static string OtherTenantOrigin => $"http://{OtherTenantHost}";

    private async Task<(Guid TenantId, Guid OtherTenantId, Guid AccountId, Guid PlayerId, Guid OtherPlayerId, Guid GuestId, Guid OtherGuestId, string Email, Guid OwnerAdminId, Guid EditorAdminId, Guid ModeratorAdminId, Guid PlatformAdminId, Guid OtherPlatformAdminId, Guid ForcedAdminId)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var otherPlayerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var otherGuestId = Guid.NewGuid();
        var ownerAdminId = Guid.NewGuid();
        var editorAdminId = Guid.NewGuid();
        var moderatorAdminId = Guid.NewGuid();
        var platformAdminId = Guid.NewGuid();
        var otherPlatformAdminId = Guid.NewGuid();
        var forcedAdminId = Guid.NewGuid();
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            delete from tenant_hosts where host = @host;
            delete from tenant_hosts where host = @otherHost;
            insert into tenants(id, slug, name, default_language) values (@tenantId, @slug, 'Security', 'en');
            insert into tenant_hosts(tenant_id, host) values (@tenantId, @host);
            insert into tenants(id, slug, name, default_language) values (@otherTenantId, @otherSlug, 'Security other', 'en');
            insert into tenant_hosts(tenant_id, host) values (@otherTenantId, @otherHost);
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, 'Account player', 'account', 'en'),
                   (@tenantId, @guestId, null, 'Guest player', 'guest', 'en'),
                   (@otherTenantId, @otherPlayerId, @accountId, 'Other account player', 'other-account', 'en'),
                   (@otherTenantId, @otherGuestId, null, 'Other guest player', 'other-guest', 'en');
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            values (@tenantId, 'feature', 'security', true, true);
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            values (@otherTenantId, 'feature', 'security', true, true);
            insert into admins(id, username, email, password_hash, must_change_password, is_platform)
            values (@ownerAdminId, @ownerUsername, @ownerEmail, 'hash', false, false),
                   (@editorAdminId, @editorUsername, @editorEmail, 'hash', false, false),
                   (@moderatorAdminId, @moderatorUsername, @moderatorEmail, 'hash', false, false),
                   (@platformAdminId, @platformUsername, @platformEmail, 'hash', false, true),
                   (@otherPlatformAdminId, @otherPlatformUsername, @otherPlatformEmail, 'hash', false, true),
                   (@forcedAdminId, @forcedUsername, @forcedEmail, 'hash', true, false);
            insert into admin_memberships(tenant_id, admin_id, role)
            values (@tenantId, @ownerAdminId, 'owner'),
                   (@tenantId, @editorAdminId, 'editor'),
                   (@tenantId, @moderatorAdminId, 'moderator'),
                   (@tenantId, @forcedAdminId, 'moderator');
            """, new
        {
            tenantId, otherTenantId, slug = $"security-{tenantId:N}", otherSlug = $"security-other-{otherTenantId:N}", host = TenantHost, otherHost = OtherTenantHost,
            accountId, email = $"security-{accountId:N}@example.test", playerId, otherPlayerId, guestId, otherGuestId,
            ownerAdminId, editorAdminId, moderatorAdminId, platformAdminId, otherPlatformAdminId, forcedAdminId,
            ownerUsername = $"owner-{ownerAdminId:N}", ownerEmail = $"owner-{ownerAdminId:N}@example.test",
            editorUsername = $"editor-{editorAdminId:N}", editorEmail = $"editor-{editorAdminId:N}@example.test",
            moderatorUsername = $"moderator-{moderatorAdminId:N}", moderatorEmail = $"moderator-{moderatorAdminId:N}@example.test",
            platformUsername = $"platform-{platformAdminId:N}", platformEmail = $"platform-{platformAdminId:N}@example.test",
            otherPlatformUsername = $"platform-other-{otherPlatformAdminId:N}", otherPlatformEmail = $"platform-other-{otherPlatformAdminId:N}@example.test",
            forcedUsername = $"forced-{forcedAdminId:N}", forcedEmail = $"forced-{forcedAdminId:N}@example.test",
        });
        return (tenantId, otherTenantId, accountId, playerId, otherPlayerId, guestId, otherGuestId, $"security-{accountId:N}@example.test", ownerAdminId, editorAdminId, moderatorAdminId, platformAdminId, otherPlatformAdminId, forcedAdminId);
    }

    private static async Task AssertBannedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<BannedError>();
        Assert.Equal("banned", error?.Error);
    }

    private static async Task AssertDeletedIdentityAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("banned", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task InsertLoginCodeAsync(string email, string code)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("platform-test-email-auth-secret"), Encoding.UTF8.GetBytes(code));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            insert into login_codes (email, code_hash, expires_at)
            values (@email, @hash, now() + interval '10 minutes')
            on conflict (email) do update set code_hash = excluded.code_hash, attempts = 0, expires_at = excluded.expires_at
            """, new { email, hash });
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, string? cookie, string host = TenantHost)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/antiforgery", cookie, null, host: host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store, no-cache, max-age=0", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<string> AdminAntiforgeryAsync(HttpClient client, string host, string cookie)
    {
        using var response = await SendAdminAsync(client, host, HttpMethod.Get, "/api/admin/antiforgery", cookie, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store, no-cache, max-age=0", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<string> IssueAdminCookieAsync(IAdminSessionIssuer sessions, Guid tenantId, Guid adminId, bool mustChangePassword = false)
    {
        var context = new DefaultHttpContext();
        Assert.True(await sessions.IssueAsync(context, tenantId, adminId, mustChangePassword));
        return CookieValue(context, "__Host-qa");
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookie,
        string? csrf,
        string? origin = null,
        HttpContent? content = null,
        string host = TenantHost)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qp={cookie}");
        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF", csrf);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAdminAsync(
        HttpClient client,
        string host,
        HttpMethod method,
        string path,
        string? cookie,
        string? csrf,
        string? origin,
        HttpContent? content = null,
        string cookieName = "__Host-qa",
        string csrfHeader = "X-CSRF-Admin")
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={cookie}");
        if (csrf is not null) request.Headers.TryAddWithoutValidation(csrfHeader, csrf);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static string CookieValue(DefaultHttpContext context, string name = "__Host-qp")
    {
        var header = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(header);
        var value = header[(name.Length + 1)..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record BannedError(string Error);
    private sealed record StaleAntiforgeryResponse(HttpStatusCode StatusCode, string? Error);
    private sealed record AuditPage(IReadOnlyList<AuditItem> Items, long? Next);
    private sealed record AuditItem(long Id, Guid? ActorId);
    private sealed record AuditRow(Guid? ActorId, string Action, string TargetType, string Data);
    private sealed record TypedAdminPayload(string Value);

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
            feature.AdminRoutes(AdminRole.Moderator, routes =>
            {
                routes.MapPost("/moderator", () => Results.Ok());
                routes.MapPost("/moderator-typed", (TypedAdminPayload _) => Results.Ok());
                routes.MapPost("/moderator-multipart", async (HttpRequest request) =>
                {
                    var form = await request.ReadFormAsync();
                    return Results.Ok(new { count = form.Count });
                });
            });
            feature.AdminRoutes(AdminRole.Editor, routes => routes.MapPost("/editor", () => Results.Ok()));
            feature.AdminRoutes(AdminRole.Owner, routes => routes.MapPost("/owner", () => Results.Ok()));
        }
    }

    private sealed record SecuritySettings;
}
