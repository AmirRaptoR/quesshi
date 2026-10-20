using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminIdentityAuditDatabaseTests(PostgresFixture fixture)
{
    private const string MigrationName = "20261015T0204_core_admin_identity_audit.sql";
    private const string LaterMigrationName = "20261015T0301_core_subject_only_external_identities.sql";
    private const string CategoryMigrationName = "20261016T0100_core_categories.sql";
    private const string MembershipActivityMigrationName = "20261018T0000_core_admin_membership_activity.sql";
    private const string QuestionsMigrationName = "20261018T0100_core_questions.sql";
    private const string MediaMigrationName = "20261019T0100_core_media_assets.sql";
    private const string SessionCreationMigrationName = "20261020T0100_core_session_creation_identity.sql";
    private static readonly Guid TenantA = Guid.Parse("8d6b19a5-9349-48ec-b63b-28e987f95158");
    private static readonly Guid TenantB = Guid.Parse("bd41c053-3e51-43ce-b842-3d625a56c7d1");

    [Fact]
    public void Core_migrations_include_admin_identity_and_audit_after_existing_core_migrations()
    {
        var names = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly).Select(migration => migration.Name).ToArray();

        Assert.Contains(MigrationName, names);
        Assert.True(Array.IndexOf(names, "20261014T0204_core_deadlines.sql") < Array.IndexOf(names, MigrationName));
    }

    [SkippableFact]
    public async Task Admin_identity_migration_upgrades_existing_sessions_without_changing_account_or_guest_subjects()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        await ResetNewContractAsync(owner);
        await InsertTenantsAsync(owner);
        var account = Guid.NewGuid();
        var guest = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@id, 'before-admin@example.test')", ("id", account));
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'Guest', 'seed', 'en')", ("tenant", TenantA), ("id", guest));
        await ExecuteAsync(owner, "insert into auth_sessions (token_hash, tenant_id, account_id, expires_at) values (@hash, @tenant, @account, now() + interval '1 hour')", ("hash", new byte[] { 1 }), ("tenant", TenantA), ("account", account));
        await ExecuteAsync(owner, "insert into auth_sessions (token_hash, tenant_id, guest_id, expires_at) values (@hash, @tenant, @guest, now() + interval '1 hour')", ("hash", new byte[] { 2 }), ("tenant", TenantA), ("guest", guest));

        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from auth_sessions where account_id = @account", ("account", account)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from auth_sessions where guest_id = @guest", ("guest", guest)));
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_constraint where conname = 'auth_sessions_admin_id_fkey')"));
    }

    [SkippableFact]
    public async Task System_maintains_admin_identity_while_app_is_denied_direct_access()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var admin = Guid.NewGuid();
        await using var system = await OpenAsync("quesshi_system");
        await ExecuteAsync(system, "insert into admins (id, username, email, password_hash) values (@id, 'administrator', 'administrator@example.test', 'hash')", ("id", admin));
        await ExecuteAsync(system, "insert into admin_memberships (tenant_id, admin_id, role) values (@tenant, @admin, 'editor')", ("tenant", TenantA), ("admin", admin));
        await ExecuteAsync(system, "insert into admin_password_resets (token_hash, admin_id, expires_at) values (@hash, @admin, now() + interval '1 hour')", ("hash", new byte[] { 3 }), ("admin", admin));
        await ExecuteAsync(system, "update admins set failed_attempts = 1 where id = @id", ("id", admin));
        await ExecuteAsync(system, "update admin_password_resets set used_at = now() where token_hash = @hash", ("hash", new byte[] { 3 }));
        await AssertSqlStateAsync(() => ExecuteAsync(system, "insert into admins (id, username, email, password_hash) values (@id, 'ADMINISTRATOR', 'other@example.test', 'hash')", ("id", Guid.NewGuid())), "23505");
        await AssertSqlStateAsync(() => ExecuteAsync(system, "insert into admin_memberships (tenant_id, admin_id, role) values (@tenant, @admin, 'viewer')", ("tenant", TenantA), ("admin", admin)), "23514");
        await AssertSqlStateAsync(() => ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, admin_id, expires_at) values (@hash, @tenant, @admin, now() + interval '1 hour')", ("hash", new byte[] { 4 }), ("tenant", TenantA), ("admin", Guid.NewGuid())), "23503");
        await ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, admin_id, expires_at) values (@hash, @tenant, @admin, now() + interval '1 hour')", ("hash", new byte[] { 5 }), ("tenant", TenantA), ("admin", admin));

        await using var app = await OpenAsync("quesshi_app");
        await SetTenantAsync(app, TenantA);
        foreach (var table in new[] { "admins", "admin_memberships", "admin_password_resets" })
        {
            await AssertSqlStateAsync(() => ScalarAsync<long>(app, $"select count(*) from {table}"), "42501");
            await AssertSqlStateAsync(() => ExecuteAsync(app, $"delete from {table}"), "42501");
        }
    }

    [SkippableFact]
    public async Task Audit_log_is_append_only_and_isolated_to_the_bound_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await ExecuteAsync(owner, "insert into audit_log (tenant_id, actor_type, action) values (@tenant, 'system', 'existing-a'), (@other, 'system', 'existing-b')", ("tenant", TenantA), ("other", TenantB));

        await using var app = await OpenAsync("quesshi_app");
        await SetTenantAsync(app, TenantA);
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from audit_log"));
        await ExecuteAsync(app, "insert into audit_log (tenant_id, actor_type, action, data) values (@tenant, 'admin', 'allowed', '{}'::jsonb)", ("tenant", TenantA));
        Assert.Equal(0L, await ScalarAsync<long>(app, "select count(*) from audit_log where tenant_id = @tenant", ("tenant", TenantB)));
        await AssertSqlStateAsync(() => ExecuteAsync(app, "insert into audit_log (tenant_id, actor_type, action) values (@tenant, 'system', 'forbidden')", ("tenant", TenantB)), "42501");
        await AssertSqlStateAsync(() => ExecuteAsync(app, "update audit_log set action = 'forbidden'"), "42501");
        await AssertSqlStateAsync(() => ExecuteAsync(app, "delete from audit_log"), "42501");
        await AssertSqlStateAsync(() => ExecuteAsync(app, "insert into audit_log (tenant_id, actor_type, action, data) values (@tenant, 'system', 'array', '[]'::jsonb)", ("tenant", TenantA)), "23514");
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from audit_log; delete from auth_sessions; delete from admin_password_resets; delete from admin_memberships; delete from admins; delete from tenants");
        await InsertTenantsAsync(owner);
        return owner;
    }

    private static async Task ResetNewContractAsync(NpgsqlConnection owner)
    {
        await RewindQuestionsMigrationAsync(owner);
        await ExecuteAsync(owner, $"alter table sessions drop column creation_roster; alter table sessions drop column creation_shape; delete from schema_migrations where name in ('{SessionCreationMigrationName}', '{MigrationName}', '{LaterMigrationName}', '{CategoryMigrationName}', '20261016T0100_core_player_bans.sql', '{MembershipActivityMigrationName}'); drop table categories; drop function revoke_player_sessions(uuid); alter table auth_sessions drop column revoked_at; drop table audit_log; alter table auth_sessions drop constraint auth_sessions_admin_id_fkey; drop table admin_password_resets; drop table admin_memberships; drop table admins");
        await ExecuteAsync(owner, "delete from tenants where id in (@a, @b)", ("a", TenantA), ("b", TenantB));
    }

    private static Task RewindQuestionsMigrationAsync(NpgsqlConnection owner)
    {
        return ExecuteAsync(owner, $"alter table session_items drop constraint if exists session_items_media_fk; alter table questions drop constraint if exists questions_media_fk; alter table session_items drop constraint if exists session_items_question_fk; alter table session_items drop constraint if exists session_items_category_fk; drop table questions; drop table media_assets; drop table ai_calls; drop table ai_budgets; drop table generation_runs; delete from schema_migrations where name in ('{QuestionsMigrationName}', '{MediaMigrationName}', '20261020T2300_ai_generation_budgets.sql')");
    }

    private static Task InsertTenantsAsync(NpgsqlConnection owner) =>
        ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'admin-a', 'Admin A', 'en'), (@b, 'admin-b', 'Admin B', 'en')", ("a", TenantA), ("b", TenantB));

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenant) =>
        ExecuteAsync(connection, "select set_config('app.tenant_id', @tenant, false)", ("tenant", tenant.ToString()));

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

    private static async Task AssertSqlStateAsync(Func<Task> action, string expectedState)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(expectedState, exception.SqlState);
    }

    private static async Task AssertSqlStateAsync<T>(Func<Task<T>> action, string expectedState)
    {
        await AssertSqlStateAsync(async () => { _ = await action(); }, expectedState);
    }
}
