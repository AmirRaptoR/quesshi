using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Identity;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Security;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminAccountManagementDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("24b68c70-16f8-4195-bc32-e547c1c9af90");
    private static readonly Guid TenantB = Guid.Parse("99f50d78-9c08-43b9-a98d-84ebf129d033");

    [SkippableFact]
    public async Task Api_lists_only_the_request_tenant_and_refuses_an_editor()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var tenantOwner = await InsertAdminAsync(owner, "api-owner", "owner@example.test");
        var editor = await InsertAdminAsync(owner, "api-editor", "editor@example.test");
        var moderator = await InsertAdminAsync(owner, "api-moderator", "moderator@example.test");
        var platform = await InsertAdminAsync(owner, "api-platform", "platform@example.test", isPlatform: true);
        var foreign = await InsertAdminAsync(owner, "api-foreign", "foreign@example.test");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await MembershipAsync(owner, TenantB, foreign, "owner");
        await ExecuteAsync(owner, "update admins set locked_until = @locked where id = @admin", ("locked", new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero)), ("admin", editor));
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
            var editorCookie = await IssueCookieAsync(sessions, TenantA, editor);
            var moderatorCookie = await IssueCookieAsync(sessions, TenantA, moderator);
            var platformCookie = await IssueCookieAsync(sessions, TenantA, platform);

            using var ownerResponse = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", ownerCookie);
            using var platformResponse = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", platformCookie);
            using var editorResponse = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", editorCookie);
            using var moderatorResponse = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", moderatorCookie);
            using var anonymousResponse = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", null);
            var listed = await ownerResponse.Content.ReadAsStringAsync();

            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized),
                (ownerResponse.StatusCode, platformResponse.StatusCode, editorResponse.StatusCode, moderatorResponse.StatusCode, anonymousResponse.StatusCode));
            Assert.DoesNotContain("api-foreign", listed, StringComparison.Ordinal);
            Assert.Contains("api-owner", listed, StringComparison.Ordinal);
            Assert.Contains("api-editor", listed, StringComparison.Ordinal);
            Assert.Contains("owner", listed, StringComparison.Ordinal);
            Assert.Contains("editor", listed, StringComparison.Ordinal);
            Assert.Contains("lockedUntil", listed, StringComparison.Ordinal);
            Assert.DoesNotContain("passwordHash", listed, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Tenant_membership_listing_and_removal_are_isolated()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        var shared = await InsertAdminAsync(owner, "shared-admin", "shared@example.test");
        var foreign = await InsertAdminAsync(owner, "foreign-admin", "foreign@example.test");
        await MembershipAsync(owner, TenantA, shared, "editor");
        await MembershipAsync(owner, TenantB, shared, "owner");
        await MembershipAsync(owner, TenantB, foreign, "owner");
        await SessionAsync(owner, TenantA, shared);
        await SessionAsync(owner, TenantB, shared);

        var before = await manager.ListAsync(TenantA);
        var removal = await manager.RemoveMembershipAsync(TenantA, shared);
        var after = await manager.ListAsync(TenantA);

        Assert.Single(before);
        Assert.Equal(AdminAccountChangeStatus.Success, removal.Status);
        Assert.Empty(after);
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admin_memberships where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantB), ("admin", shared)));
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from auth_sessions where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantB), ("admin", shared)));
    }

    [SkippableFact]
    public async Task Listing_exposes_the_persisted_lockout_status()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var admin = await InsertAdminAsync(owner, "locked-admin", "locked-admin@example.test");
        var lockedUntil = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        await MembershipAsync(owner, TenantA, admin, "editor");
        await ExecuteAsync(owner, "update admins set locked_until = @locked where id = @admin", ("locked", lockedUntil), ("admin", admin));

        var account = Assert.Single(await Manager(sources).ListAsync(TenantA));

        Assert.Equal(lockedUntil, account.LockedUntil);
    }

    [SkippableFact]
    public async Task Invalid_or_duplicate_creation_does_not_create_an_identity_or_membership()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        await InsertAdminAsync(owner, "existing-admin", "existing@example.test");

        var invalid = await manager.CreateAsync(TenantA, new CreateManagedAdmin("no", "not-an-email", "short", false));
        var duplicate = await manager.CreateAsync(TenantA, new CreateManagedAdmin("existing-admin", "new@example.test", "Strong password 2026!", false));

        Assert.Equal(AdminAccountChangeStatus.InvalidUsername, invalid.Status);
        Assert.Equal(AdminAccountChangeStatus.UsernameTaken, duplicate.Status);
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admins"));
        Assert.Equal(0, await ScalarAsync<int>(owner, "select count(*) from admin_memberships"));
    }

    [SkippableFact]
    public async Task Create_maps_a_database_username_unique_violation_to_username_taken()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await InsertAdminAsync(owner, "existing-race-user", "existing-race-user@example.test");
        await InstallForcedUniqueViolationTriggerAsync(owner);
        try
        {
            await using var sources = Sources();
            var result = await Manager(sources).CreateAsync(TenantA, new CreateManagedAdmin("forced-username", "fresh-username@example.test", "Strong password 2026!", false));

            Assert.Equal(AdminAccountChangeStatus.UsernameTaken, result.Status);
        }
        finally
        {
            await RemoveForcedUniqueViolationTriggerAsync(owner);
        }
    }

    [SkippableFact]
    public async Task Create_maps_a_database_email_unique_violation_to_email_taken()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await InsertAdminAsync(owner, "existing-race-email", "existing-race-email@example.test");
        await InstallForcedUniqueViolationTriggerAsync(owner);
        try
        {
            await using var sources = Sources();
            var result = await Manager(sources).CreateAsync(TenantA, new CreateManagedAdmin("forced-email", "fresh-email@example.test", "Strong password 2026!", false));

            Assert.Equal(AdminAccountChangeStatus.EmailTaken, result.Status);
        }
        finally
        {
            await RemoveForcedUniqueViolationTriggerAsync(owner);
        }
    }

    [SkippableFact]
    public async Task Creation_adds_an_owner_membership_and_requires_a_password_change()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var result = await Manager(sources).CreateAsync(TenantA, new CreateManagedAdmin("new-admin", "new-admin@example.test", "Strong password 2026!", false));

        Assert.Equal(AdminAccountChangeStatus.Success, result.Status);
        Assert.NotNull(result.Account);
        Assert.True(result.Account.MustChangePassword);
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admin_memberships where tenant_id = @tenant and admin_id = @admin and role = 'owner'", ("tenant", TenantA), ("admin", result.Account.Id)));
        Assert.True(await ScalarAsync<bool>(owner, "select must_change_password from admins where id = @admin", ("admin", result.Account.Id)));
    }

    [SkippableFact]
    public async Task Api_creates_an_owner_membership_for_the_request_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "creation-owner", "creation-owner@example.test");
        await MembershipAsync(owner, TenantA, actor, "owner");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var cookie = await IssueCookieAsync(sessions, TenantA, actor);
            var token = await AdminAntiforgeryAsync(client, cookie);
            using var body = JsonContent.Create(new { username = "created-through-api", email = "created-through-api@example.test", password = "Strong password 2026!" });
            using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", cookie, token, "http://accounts-a.example.test", body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admin_memberships m join admins a on a.id = m.admin_id where m.tenant_id = @tenant and a.username = 'created-through-api' and m.role = 'owner'", ("tenant", TenantA)));
            Assert.True(await ScalarAsync<bool>(owner, "select must_change_password from admins where username = 'created-through-api'"));
            var payload = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Strong password 2026!", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("passwordHash", payload, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Api_creation_enforces_tenant_and_identity_validation_rules()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var tenantOwner = await InsertAdminAsync(owner, "validation-owner", "validation-owner@example.test");
        var platform = await InsertAdminAsync(owner, "validation-platform", "validation-platform@example.test", isPlatform: true);
        var inactive = await InsertAdminAsync(owner, "inactive-existing", "inactive-existing@example.test");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await ExecuteAsync(owner, "update admins set is_active = false where id = @admin", ("admin", inactive));
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
            var platformCookie = await IssueCookieAsync(sessions, TenantA, platform);
            var ownerToken = await AdminAntiforgeryAsync(client, ownerCookie);
            var platformToken = await AdminAntiforgeryAsync(client, platformCookie);
            using var platformBody = JsonContent.Create(new { username = "forbidden-platform", email = "forbidden-platform@example.test", password = "Strong password 2026!", isPlatform = true });
            using var duplicateEmailBody = JsonContent.Create(new { username = "fresh-email", email = "inactive-existing@example.test", password = "Strong password 2026!" });
            using var invalidEmailBody = JsonContent.Create(new { username = "bad-email", email = "invalid", password = "Strong password 2026!" });
            using var shortUsernameBody = JsonContent.Create(new { username = "ab", email = "short@example.test", password = "Strong password 2026!" });
            using var longUsernameBody = JsonContent.Create(new { username = new string('a', 33), email = "long@example.test", password = "Strong password 2026!" });
            using var weakPasswordBody = JsonContent.Create(new { username = "weak-password", email = "weak@example.test", password = "short" });
            using var duplicateInactiveBody = JsonContent.Create(new { username = "inactive-existing", email = "fresh-inactive@example.test", password = "Strong password 2026!" });
            using var allowedPlatformBody = JsonContent.Create(new { username = "allowed-platform", email = "allowed-platform@example.test", password = "Strong password 2026!", isPlatform = true });
            using var forbiddenPlatform = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", platformBody);
            using var duplicateEmail = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", duplicateEmailBody);
            using var invalidEmail = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", invalidEmailBody);
            using var shortUsername = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", shortUsernameBody);
            using var longUsername = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", longUsernameBody);
            using var weakPassword = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", weakPasswordBody);
            using var duplicateInactive = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", duplicateInactiveBody);
            using var allowedPlatform = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", platformCookie, platformToken, "http://accounts-a.example.test", allowedPlatformBody);

            Assert.Equal(HttpStatusCode.Forbidden, forbiddenPlatform.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, duplicateEmail.StatusCode);
            Assert.Contains("admin.auth.emailTaken", await duplicateEmail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, invalidEmail.StatusCode);
            Assert.Contains("admin.auth.badEmail", await invalidEmail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, shortUsername.StatusCode);
            Assert.Contains("admin.auth.badUsername", await shortUsername.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, longUsername.StatusCode);
            Assert.Contains("admin.auth.badUsername", await longUsername.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);
            Assert.Contains("admin.auth.weak", await weakPassword.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, duplicateInactive.StatusCode);
            Assert.Contains("admin.auth.usernameTaken", await duplicateInactive.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, allowedPlatform.StatusCode);
            Assert.Equal(4, await ScalarAsync<int>(owner, "select count(*) from admins"));
            Assert.Equal(2, await ScalarAsync<int>(owner, "select count(*) from admin_memberships"));
            Assert.Equal(0, await ScalarAsync<int>(owner, "select count(*) from admin_memberships where admin_id = @admin", ("admin", inactive)));
            Assert.True(await ScalarAsync<bool>(owner, "select is_platform from admins where username = 'allowed-platform'"));
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admin_memberships m join admins a on a.id = m.admin_id where m.tenant_id = @tenant and a.username = 'allowed-platform'", ("tenant", TenantA)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Api_rejects_invalid_roles_and_other_tenant_memberships()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "role-owner", "role-owner@example.test");
        var foreign = await InsertAdminAsync(owner, "role-foreign", "role-foreign@example.test");
        await MembershipAsync(owner, TenantA, actor, "owner");
        await MembershipAsync(owner, TenantB, foreign, "editor");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var cookie = await IssueCookieAsync(sessions, TenantA, actor);
            var token = await AdminAntiforgeryAsync(client, cookie);
            using var invalidBody = JsonContent.Create(new { role = "player" });
            using var foreignBody = JsonContent.Create(new { role = "editor" });
            using var invalidResponse = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{actor}/role", cookie, token, "http://accounts-a.example.test", invalidBody);
            using var foreignResponse = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{foreign}/role", cookie, token, "http://accounts-a.example.test", foreignBody);

            Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (invalidResponse.StatusCode, foreignResponse.StatusCode));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Api_changes_only_the_request_tenant_membership_role()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "role-change-owner", "role-change-owner@example.test");
        var platform = await InsertAdminAsync(owner, "role-change-platform", "role-change-platform@example.test", isPlatform: true);
        var target = await InsertAdminAsync(owner, "role-change-target", "role-change-target@example.test");
        await MembershipAsync(owner, TenantA, actor, "owner");
        await MembershipAsync(owner, TenantA, target, "editor");
        await MembershipAsync(owner, TenantB, target, "editor");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var actorCookie = await IssueCookieAsync(sessions, TenantA, actor);
            var platformCookie = await IssueCookieAsync(sessions, TenantA, platform);
            var actorToken = await AdminAntiforgeryAsync(client, actorCookie);
            var platformToken = await AdminAntiforgeryAsync(client, platformCookie);
            using var ownerBody = JsonContent.Create(new { role = "owner" });
            using var editorBody = JsonContent.Create(new { role = "editor" });
            using var moderatorBody = JsonContent.Create(new { role = "moderator" });
            using var ownerResponse = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{target}/role", actorCookie, actorToken, "http://accounts-a.example.test", ownerBody);
            var afterOwner = await ScalarAsync<string>(owner, "select role from admin_memberships where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantA), ("admin", target));
            using var editorResponse = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{target}/role", platformCookie, platformToken, "http://accounts-a.example.test", editorBody);
            var afterEditor = await ScalarAsync<string>(owner, "select role from admin_memberships where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantA), ("admin", target));
            using var moderatorResponse = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{target}/role", actorCookie, actorToken, "http://accounts-a.example.test", moderatorBody);

            Assert.Equal(HttpStatusCode.NoContent, ownerResponse.StatusCode);
            Assert.Equal("owner", afterOwner);
            Assert.Equal(HttpStatusCode.NoContent, editorResponse.StatusCode);
            Assert.Equal("editor", afterEditor);
            Assert.Equal(HttpStatusCode.NoContent, moderatorResponse.StatusCode);
            Assert.Equal("moderator", await ScalarAsync<string>(owner, "select role from admin_memberships where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantA), ("admin", target)));
            Assert.Equal("editor", await ScalarAsync<string>(owner, "select role from admin_memberships where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantB), ("admin", target)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Concurrent_owner_role_downgrades_leave_an_active_owner()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        var first = await InsertAdminAsync(owner, "downgrade-first", "downgrade-first@example.test");
        var second = await InsertAdminAsync(owner, "downgrade-second", "downgrade-second@example.test");
        await MembershipAsync(owner, TenantA, first, "owner");
        await MembershipAsync(owner, TenantA, second, "owner");

        var results = await Task.WhenAll(manager.ChangeRoleAsync(TenantA, first, Quesshi.Plugin.Api.AdminRole.Editor), manager.ChangeRoleAsync(TenantA, second, Quesshi.Plugin.Api.AdminRole.Moderator));

        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.Success);
        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.LastOwner);
        Assert.Equal(1, await ScalarAsync<int>(owner, """
            select count(*) from admin_memberships m join admins a on a.id = m.admin_id
            where m.tenant_id = @tenant and m.role = 'owner' and a.is_active
            """, ("tenant", TenantA)));
    }

    [SkippableFact]
    public async Task Api_removal_revokes_only_the_current_tenant_sessions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "removal-owner", "removal-owner@example.test");
        var target = await InsertAdminAsync(owner, "removal-target", "removal-target@example.test");
        await MembershipAsync(owner, TenantA, actor, "owner");
        await MembershipAsync(owner, TenantA, target, "editor");
        await MembershipAsync(owner, TenantB, target, "owner");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var cookie = await IssueCookieAsync(sessions, TenantA, actor);
            var targetTenantACookie = await IssueCookieAsync(sessions, TenantA, target);
            var targetTenantBCookie = await IssueCookieAsync(sessions, TenantB, target);
            var token = await AdminAntiforgeryAsync(client, cookie);
            using var response = await SendAsync(client, HttpMethod.Delete, $"/api/admin/accounts/{target}", cookie, token, "http://accounts-a.example.test");
            using var revoked = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", targetTenantACookie);
            using var retained = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", targetTenantBCookie, host: "accounts-b.example.test");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            Assert.Equal((0, 1), (
                await ScalarAsync<int>(owner, "select count(*) from auth_sessions where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantA), ("admin", target)),
                await ScalarAsync<int>(owner, "select count(*) from auth_sessions where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantB), ("admin", target))));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Concurrent_owner_removals_leave_an_active_owner()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        var first = await InsertAdminAsync(owner, "first-owner", "first@example.test");
        var second = await InsertAdminAsync(owner, "second-owner", "second@example.test");
        await MembershipAsync(owner, TenantA, first, "owner");
        await MembershipAsync(owner, TenantA, second, "owner");

        var results = await Task.WhenAll(manager.RemoveMembershipAsync(TenantA, first), manager.RemoveMembershipAsync(TenantA, second));

        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.Success);
        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.LastOwner);
        Assert.Equal(1, await ScalarAsync<int>(owner, """
            select count(*) from admin_memberships m join admins a on a.id = m.admin_id
            where m.tenant_id = @tenant and m.role = 'owner' and a.is_active
            """, ("tenant", TenantA)));
    }

    [SkippableFact]
    public async Task Global_deactivation_revokes_all_sessions_without_restoring_them()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "platform-actor", "platform@example.test", isPlatform: true);
        var target = await InsertAdminAsync(owner, "shared-target", "target@example.test");
        var tenantABackup = await InsertAdminAsync(owner, "tenant-a-backup", "tenant-a-backup@example.test");
        var tenantBBackup = await InsertAdminAsync(owner, "tenant-b-backup", "tenant-b-backup@example.test");
        await MembershipAsync(owner, TenantA, target, "owner");
        await MembershipAsync(owner, TenantA, tenantABackup, "owner");
        await MembershipAsync(owner, TenantB, target, "owner");
        await MembershipAsync(owner, TenantB, tenantBBackup, "owner");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var actorCookie = await IssueCookieAsync(sessions, TenantA, actor);
            var targetTenantACookie = await IssueCookieAsync(sessions, TenantA, target);
            var targetTenantBCookie = await IssueCookieAsync(sessions, TenantB, target);
            var actorToken = await AdminAntiforgeryAsync(client, actorCookie);
            using var deactivation = await SendAsync(client, HttpMethod.Post, $"/api/admin/accounts/{target}/active?value=false", actorCookie, actorToken, "http://accounts-a.example.test");
            using var tenantAAfterDeactivation = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", targetTenantACookie);
            using var tenantBAfterDeactivation = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", targetTenantBCookie, host: "accounts-b.example.test");
            using var reactivation = await SendAsync(client, HttpMethod.Post, $"/api/admin/accounts/{target}/active?value=true", actorCookie, actorToken, "http://accounts-a.example.test");
            using var tenantAAfterReactivation = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", targetTenantACookie);
            var freshTenantACookie = await IssueCookieAsync(sessions, TenantA, target);
            using var freshTenantA = await SendAsync(client, HttpMethod.Get, "/api/admin/accounts", freshTenantACookie);

            Assert.Equal(HttpStatusCode.NoContent, deactivation.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, tenantAAfterDeactivation.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, tenantBAfterDeactivation.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, reactivation.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, tenantAAfterReactivation.StatusCode);
            Assert.Equal(HttpStatusCode.OK, freshTenantA.StatusCode);
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from auth_sessions where admin_id = @admin", ("admin", target)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Concurrent_platform_deactivations_leave_an_active_platform_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        var first = await InsertAdminAsync(owner, "first-platform", "first-platform@example.test", isPlatform: true);
        var second = await InsertAdminAsync(owner, "second-platform", "second-platform@example.test", isPlatform: true);

        var results = await Task.WhenAll(manager.SetActiveAsync(first, second, false), manager.SetActiveAsync(second, first, false));

        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.Success);
        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.LastPlatformAdmin);
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admins where is_platform and is_active"));
    }

    [SkippableFact]
    public async Task Concurrent_platform_removals_leave_an_active_platform_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        var first = await InsertAdminAsync(owner, "first-global-platform", "first-global-platform@example.test", isPlatform: true);
        var second = await InsertAdminAsync(owner, "second-global-platform", "second-global-platform@example.test", isPlatform: true);

        var results = await Task.WhenAll(manager.RemoveGlobalAsync(first, second), manager.RemoveGlobalAsync(second, first));

        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.Success);
        Assert.Contains(results, result => result.Status == AdminAccountChangeStatus.LastPlatformAdmin);
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admins where is_platform and is_active"));
    }

    [SkippableFact]
    public async Task Role_downgrade_and_global_removal_keep_the_last_active_owner()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var manager = Manager(sources);
        var platform = await InsertAdminAsync(owner, "platform-remover", "platform-remover@example.test", isPlatform: true);
        var tenantOwner = await InsertAdminAsync(owner, "sole-owner", "sole-owner@example.test");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");

        var downgrade = await manager.ChangeRoleAsync(TenantA, tenantOwner, Quesshi.Plugin.Api.AdminRole.Editor);
        var deactivation = await manager.SetActiveAsync(platform, tenantOwner, false);
        var removal = await manager.RemoveGlobalAsync(platform, tenantOwner);

        Assert.Equal(AdminAccountChangeStatus.LastOwner, downgrade.Status);
        Assert.Equal(AdminAccountChangeStatus.LastOwner, deactivation.Status);
        Assert.Equal(AdminAccountChangeStatus.LastOwner, removal.Status);
        Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from admins where id = @admin and is_active", ("admin", tenantOwner)));
    }

    [SkippableFact]
    public async Task Global_changes_cannot_target_the_calling_platform_admin()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var platform = await InsertAdminAsync(owner, "self-platform", "self-platform@example.test", isPlatform: true);
        var manager = Manager(sources);

        var deactivate = await manager.SetActiveAsync(platform, platform, false);
        var remove = await manager.RemoveGlobalAsync(platform, platform);

        Assert.Equal((AdminAccountChangeStatus.CannotChangeSelf, AdminAccountChangeStatus.CannotDeleteSelf), (deactivate.Status, remove.Status));
    }

    [SkippableFact]
    public async Task Api_requires_a_platform_admin_for_global_changes_and_audits_each_attempt_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var tenantOwner = await InsertAdminAsync(owner, "tenant-owner", "tenant-owner@example.test");
        var platform = await InsertAdminAsync(owner, "platform-admin", "platform-admin@example.test", isPlatform: true);
        var target = await InsertAdminAsync(owner, "active-target", "active-target@example.test");
        var backup = await InsertAdminAsync(owner, "owner-backup", "owner-backup@example.test");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await MembershipAsync(owner, TenantA, target, "owner");
        await MembershipAsync(owner, TenantA, backup, "owner");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
            var platformCookie = await IssueCookieAsync(sessions, TenantA, platform);
            var ownerToken = await AdminAntiforgeryAsync(client, ownerCookie);
            var platformToken = await AdminAntiforgeryAsync(client, platformCookie);

            using var tenantOwnerResponse = await SendAsync(client, HttpMethod.Post, $"/api/admin/accounts/{target}/active?value=false", ownerCookie, ownerToken, "http://accounts-a.example.test");
            using var platformResponse = await SendAsync(client, HttpMethod.Post, $"/api/admin/accounts/{target}/active?value=false", platformCookie, platformToken, "http://accounts-a.example.test");

            Assert.Equal(HttpStatusCode.Forbidden, tenantOwnerResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, platformResponse.StatusCode);
            Assert.Equal(2, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant", ("tenant", TenantA)));
            Assert.Equal("admin.account.active", await ScalarAsync<string>(owner, "select action from audit_log where tenant_id = @tenant order by id desc limit 1", ("tenant", TenantA)));
            Assert.Equal(target, await ScalarAsync<Guid>(owner, "select target_id from audit_log where tenant_id = @tenant order by id desc limit 1", ("tenant", TenantA)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Api_account_writes_audit_the_actor_action_and_target_without_sensitive_data()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var tenantOwner = await InsertAdminAsync(owner, "audit-owner", "audit-owner@example.test");
        var platform = await InsertAdminAsync(owner, "audit-platform", "audit-platform@example.test", isPlatform: true);
        var member = await InsertAdminAsync(owner, "audit-member", "audit-member@example.test");
        var globalTarget = await InsertAdminAsync(owner, "audit-global", "audit-global@example.test");
        var refusedGlobalTarget = await InsertAdminAsync(owner, "audit-forbidden", "audit-forbidden@example.test");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await MembershipAsync(owner, TenantA, member, "editor");
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
            var platformCookie = await IssueCookieAsync(sessions, TenantA, platform);
            var ownerToken = await AdminAntiforgeryAsync(client, ownerCookie);
            var platformToken = await AdminAntiforgeryAsync(client, platformCookie);
            const string password = "Audit password 2026!";
            using var createBody = JsonContent.Create(new { username = "audit-created", email = "audit-created@example.test", password });
            using var create = await SendAsync(client, HttpMethod.Post, "/api/admin/accounts", ownerCookie, ownerToken, "http://accounts-a.example.test", createBody);
            using var roleBody = JsonContent.Create(new { role = "moderator" });
            using var role = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{member}/role", ownerCookie, ownerToken, "http://accounts-a.example.test", roleBody);
            using var removal = await SendAsync(client, HttpMethod.Delete, $"/api/admin/accounts/{member}", ownerCookie, ownerToken, "http://accounts-a.example.test");
            using var globalRemoval = await SendAsync(client, HttpMethod.Delete, $"/api/admin/accounts/{globalTarget}/global", platformCookie, platformToken, "http://accounts-a.example.test");
            using var refusedGlobalRemoval = await SendAsync(client, HttpMethod.Delete, $"/api/admin/accounts/{refusedGlobalTarget}/global", ownerCookie, ownerToken, "http://accounts-a.example.test");
            using var missingTokenRole = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{tenantOwner}/role", ownerCookie, origin: "http://accounts-a.example.test", content: JsonContent.Create(new { role = "editor" }));
            using var foreignOriginRole = await SendAsync(client, HttpMethod.Put, $"/api/admin/accounts/{tenantOwner}/role", ownerCookie, ownerToken, "http://foreign.example.test", JsonContent.Create(new { role = "editor" }));

            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, globalRemoval.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, refusedGlobalRemoval.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missingTokenRole.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, foreignOriginRole.StatusCode);
            Assert.Equal("owner", await ScalarAsync<string>(owner, "select role from admin_memberships where tenant_id = @tenant and admin_id = @admin", ("tenant", TenantA), ("admin", tenantOwner)));
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and actor_id = @actor and action = 'admin.account.create' and target_type = 'admin' and target_id is null", ("tenant", TenantA), ("actor", tenantOwner)));
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and actor_id = @actor and action = 'admin.membership.role' and target_id = @target", ("tenant", TenantA), ("actor", tenantOwner), ("target", member)));
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and actor_id = @actor and action = 'admin.membership.remove' and target_id = @target", ("tenant", TenantA), ("actor", tenantOwner), ("target", member)));
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and actor_id = @actor and action = 'admin.account.remove' and target_id = @target", ("tenant", TenantA), ("actor", platform), ("target", globalTarget)));
            Assert.Equal(1, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and actor_id = @actor and action = 'admin.account.remove' and target_id = @target", ("tenant", TenantA), ("actor", tenantOwner), ("target", refusedGlobalTarget)));
            Assert.Equal(2, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and actor_id = @actor and action = 'admin.membership.role' and target_id = @target", ("tenant", TenantA), ("actor", tenantOwner), ("target", tenantOwner)));
            Assert.Equal(7, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant", ("tenant", TenantA)));
            Assert.Equal(0, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and data::text ilike @secret", ("tenant", TenantA), ("secret", $"%{password}%")));
            Assert.Equal(0, await ScalarAsync<int>(owner, "select count(*) from audit_log where tenant_id = @tenant and data::text ilike '%password_hash%'", ("tenant", TenantA)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Api_global_removal_deletes_the_identity_and_its_sessions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "global-remover", "global-remover@example.test", isPlatform: true);
        var target = await InsertAdminAsync(owner, "global-target", "global-target@example.test");
        var tenantOwner = await InsertAdminAsync(owner, "global-owner", "global-owner@example.test");
        await MembershipAsync(owner, TenantA, target, "editor");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await SessionAsync(owner, TenantA, target);
        await using var app = Quesshi.Server.Program.CreateApplication(sources, 11157, 30157);
        app.Urls.Add("http://127.0.0.1:50157");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50157") };
            var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
            var cookie = await IssueCookieAsync(sessions, TenantA, actor);
            var token = await AdminAntiforgeryAsync(client, cookie);
            using var response = await SendAsync(client, HttpMethod.Delete, $"/api/admin/accounts/{target}/global", cookie, token, "http://accounts-a.example.test");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal((0, 0), (
                await ScalarAsync<int>(owner, "select count(*) from admins where id = @admin", ("admin", target)),
                await ScalarAsync<int>(owner, "select count(*) from auth_sessions where admin_id = @admin", ("admin", target))));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    public async Task InitializeAsync() => await Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null) return;
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from auth_sessions; delete from admin_memberships; delete from admins; delete from tenant_languages; delete from tenants");
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static AdminAccountManager Manager(DatabaseSources sources) => new(sources, new IdentityPasswordHasher(), new TestClock());

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Quesshi.Server.Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from auth_sessions; delete from admin_memberships; delete from admins; delete from tenant_languages; delete from tenants");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'accounts-a', 'Accounts A', 'en'), (@b, 'accounts-b', 'Accounts B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_hosts (tenant_id, host) values (@a, 'accounts-a.example.test'), (@b, 'accounts-b.example.test')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private static async Task<Guid> InsertAdminAsync(NpgsqlConnection owner, string username, string email, bool isPlatform = false)
    {
        var id = Guid.NewGuid();
        var hasher = new IdentityPasswordHasher();
        await ExecuteAsync(owner, """
            insert into admins (id, username, email, password_hash, is_platform)
            values (@id, @username, @email, @passwordHash, @isPlatform)
            """, ("id", id), ("username", username), ("email", email), ("passwordHash", hasher.Hash("Strong password 2026!")), ("isPlatform", isPlatform));
        return id;
    }

    private static Task MembershipAsync(NpgsqlConnection owner, Guid tenant, Guid admin, string role) =>
        ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id, role) values (@tenant, @admin, @role)", ("tenant", tenant), ("admin", admin), ("role", role));

    private static Task SessionAsync(NpgsqlConnection owner, Guid tenant, Guid admin) =>
        ExecuteAsync(owner, "insert into auth_sessions (token_hash, tenant_id, admin_id, expires_at) values (@hash, @tenant, @admin, now() + interval '1 hour')", ("hash", Guid.NewGuid().ToByteArray()), ("tenant", tenant), ("admin", admin));

    private static async Task InstallForcedUniqueViolationTriggerAsync(NpgsqlConnection owner)
    {
        await ExecuteAsync(owner, "drop trigger if exists force_admin_unique_violation on admins; drop function if exists force_admin_unique_violation()");
        await ExecuteAsync(owner, """
            create function force_admin_unique_violation() returns trigger language plpgsql as $$
            begin
                if new.username = 'forced-username' then new.username := 'existing-race-user'; end if;
                if new.username = 'forced-email' then new.email := 'existing-race-email@example.test'; end if;
                return new;
            end;
            $$;
            create trigger force_admin_unique_violation before insert on admins
            for each row execute function force_admin_unique_violation()
            """);
    }

    private static Task RemoveForcedUniqueViolationTriggerAsync(NpgsqlConnection owner) =>
        ExecuteAsync(owner, "drop trigger if exists force_admin_unique_violation on admins; drop function if exists force_admin_unique_violation()");

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
        var result = (await command.ExecuteScalarAsync())!;
        return result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<string> IssueCookieAsync(IAdminSessionIssuer sessions, Guid tenant, Guid admin)
    {
        var context = new DefaultHttpContext();
        Assert.True(await sessions.IssueAsync(context, tenant, admin, false));
        var header = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(header);
        var value = header["__Host-qa=".Length..];
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private static async Task<string> AdminAntiforgeryAsync(HttpClient client, string cookie)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/antiforgery", cookie);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? cookie, string? antiforgery = null, string? origin = null, HttpContent? content = null, string host = "accounts-a.example.test")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        request.Content = content;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qa={cookie}");
        if (antiforgery is not null) request.Headers.TryAddWithoutValidation("X-CSRF-Admin", antiforgery);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private sealed record AntiforgeryResponse(string Token);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    }
}
