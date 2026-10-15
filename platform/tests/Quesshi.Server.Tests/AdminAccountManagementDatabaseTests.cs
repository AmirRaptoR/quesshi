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
    public async Task Api_removal_revokes_only_the_current_tenant_sessions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var actor = await InsertAdminAsync(owner, "removal-owner", "removal-owner@example.test");
        var target = await InsertAdminAsync(owner, "removal-target", "removal-target@example.test");
        await MembershipAsync(owner, TenantA, actor, "owner");
        await MembershipAsync(owner, TenantA, target, "editor");
        await MembershipAsync(owner, TenantB, target, "editor");
        await SessionAsync(owner, TenantA, target);
        await SessionAsync(owner, TenantB, target);
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
            using var response = await SendAsync(client, HttpMethod.Delete, $"/api/admin/accounts/{target}", cookie, token, "http://accounts-a.example.test");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
        var manager = Manager(sources);
        var actor = await InsertAdminAsync(owner, "platform-actor", "platform@example.test", isPlatform: true);
        var target = await InsertAdminAsync(owner, "shared-target", "target@example.test");
        var backup = await InsertAdminAsync(owner, "tenant-backup", "backup@example.test");
        await MembershipAsync(owner, TenantA, target, "owner");
        await MembershipAsync(owner, TenantA, backup, "owner");
        await MembershipAsync(owner, TenantB, target, "editor");
        await SessionAsync(owner, TenantA, target);
        await SessionAsync(owner, TenantB, target);

        var deactivation = await manager.SetActiveAsync(actor, target, false);
        var reactivation = await manager.SetActiveAsync(actor, target, true);

        Assert.Equal(AdminAccountChangeStatus.Success, deactivation.Status);
        Assert.Equal(AdminAccountChangeStatus.Success, reactivation.Status);
        Assert.Equal(0, await ScalarAsync<int>(owner, "select count(*) from auth_sessions where admin_id = @admin", ("admin", target)));
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? cookie, string? antiforgery = null, string? origin = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Host = "accounts-a.example.test";
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
