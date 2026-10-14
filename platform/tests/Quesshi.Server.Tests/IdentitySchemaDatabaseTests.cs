using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class IdentitySchemaDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("c66dbf5d-a7e8-42a0-9215-7c87b00bc13e");
    private static readonly Guid TenantB = Guid.Parse("b2461b37-f799-4a3b-96b3-ff5446c64d95");

    [Fact]
    public void Core_migrations_include_the_identity_security_contract_after_shipped_core_migrations()
    {
        var names = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly).Select(migration => migration.Name).ToArray();

        Assert.Contains("20261013T0202_core_identity_security.sql", names);
        Assert.True(Array.IndexOf(names, "20261013T0201_core_job_schedule_windows.sql") < Array.IndexOf(names, "20261013T0202_core_identity_security.sql"));
    }

    [SkippableFact]
    public async Task Identity_migration_preserves_existing_accounts_and_players_and_is_idempotent()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareBeforeIdentityMigrationAsync();
        var account = Guid.NewGuid();
        var player = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, 'existing@example.test')", ("account", account));
        await InsertPlayerAsync(owner, TenantA, player, account);

        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        foreach (var table in new[] { "account_identities", "login_codes", "auth_sessions", "auth_rate_limits", "rate_limits", "data_protection_keys" })
            Assert.True(await ScalarAsync<bool>(owner, "select to_regclass(@table) is not null", ("table", table)));
        foreach (var function in new[] { "account_players(uuid)", "claim_account(uuid)" })
            Assert.True(await ScalarAsync<bool>(owner, "select to_regprocedure(@function) is not null", ("function", function)));
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from accounts where id = @account)", ("account", account)));
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from players where tenant_id = @tenant and id = @player)", ("tenant", TenantA), ("player", player)));

        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
    }

    [SkippableFact]
    public async Task Roles_and_account_players_keep_global_identity_and_tenant_data_separate()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var account = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var deletedProfileAccount = Guid.NewGuid();
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, 'account@example.test'), (@other, 'other@example.test'), (@deleted, 'deleted-profile@example.test')",
            ("account", account), ("other", otherAccount), ("deleted", deletedProfileAccount));
        await InsertPlayerAsync(owner, TenantA, playerA, account, "A player");
        await InsertPlayerAsync(owner, TenantB, playerB, account, "B player");
        await InsertPlayerAsync(owner, TenantA, Guid.NewGuid(), deletedProfileAccount, "Deleted player", deleted: true);

        await using var app = await OpenAsync("quesshi_app");
        await SetTenantAsync(app, TenantA);
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from accounts"));
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from accounts where id = @account", ("account", account)));
        Assert.Equal(0L, await ScalarAsync<long>(app, "select count(*) from accounts where id = @account", ("account", deletedProfileAccount)));
        await AssertSqlStateAsync(() => ExecuteAsync(app, "update accounts set email = 'forbidden@example.test' where id = @account", ("account", account)), "42501");
        foreach (var table in new[] { "account_identities", "login_codes", "auth_sessions", "data_protection_keys" })
            await AssertSqlStateAsync(() => ScalarAsync<long>(app, $"select count(*) from {table}"), "42501");
        await AssertSqlStateAsync(() => ExecuteAsync(app, "insert into data_protection_keys (friendly_name, xml) values ('app-key', '<key />')"), "42501");
        await AssertSqlStateAsync(() => ScalarAsync<long>(app, "select count(*) from account_players(@account)", ("account", account)), "42501");

        await using var system = await OpenAsync("quesshi_system");
        await ExecuteAsync(system, "insert into account_identities (provider, subject, account_id) values ('google', 'subject', @account)", ("account", account));
        await ExecuteAsync(system, "insert into login_codes (email, code_hash, expires_at) values ('code@example.test', @hash, now() + interval '10 minutes')", ("hash", new byte[] { 1, 2, 3 }));
        await ExecuteAsync(system, "insert into data_protection_keys (friendly_name, xml) values ('key', '<key />')");
        Assert.Equal(2L, await ScalarAsync<long>(system, "select count(*) from account_players(@account)", ("account", account)));
        Assert.Equal("A player", await ScalarAsync<string>(system, "select display_name from account_players(@account) where tenant_id = @tenant", ("account", account), ("tenant", TenantA)));
        await AssertSqlStateAsync(() => ScalarAsync<long>(system, "select count(*) from rate_limits"), "42501");
    }

    [SkippableFact]
    public async Task Claim_account_rejects_unavailable_accounts_and_blocks_deletion_until_commit()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var account = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, 'claim@example.test')", ("account", account));
        await ExecuteAsync(owner, "insert into accounts (id, deleted_at) values (@account, now())", ("account", deleted));

        await using var app = await OpenAsync("quesshi_app");
        await SetTenantAsync(app, TenantA);
        await AssertSqlStateAsync(() => ExecuteAsync(app, "select claim_account(@account)", ("account", Guid.NewGuid())), "P0002");
        await AssertSqlStateAsync(() => ExecuteAsync(app, "select claim_account(@account)", ("account", deleted)), "P0002");

        await using var claimTransaction = await app.BeginTransactionAsync();
        await ExecuteAsync(app, "select claim_account(@account)", claimTransaction, ("account", account));
        await using var deleting = await OpenAsync("quesshi_owner");
        await using var deleteTransaction = await deleting.BeginTransactionAsync();
        var deletion = ExecuteAsync(deleting, "select 1 from accounts where id = @account for update; update accounts set email = null, deleted_at = now() where id = @account", deleteTransaction, ("account", account));
        await Task.Delay(100);
        Assert.False(deletion.IsCompleted);

        await claimTransaction.CommitAsync();
        await deletion;
        await deleteTransaction.CommitAsync();
        await AssertSqlStateAsync(() => ExecuteAsync(app, "select claim_account(@account)", ("account", account)), "P0002");
    }

    [SkippableFact]
    public async Task Session_and_code_tables_enforce_hashed_subject_and_tenant_constraints()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var account = Guid.NewGuid();
        var guestA = Guid.NewGuid();
        var guestB = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into accounts (id, email) values (@account, 'session@example.test')", ("account", account));
        await InsertPlayerAsync(owner, TenantA, guestA, null, "Guest A");
        await InsertPlayerAsync(owner, TenantB, guestB, null, "Guest B");

        await using var system = await OpenAsync("quesshi_system");
        await ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, account_id, expires_at) values (@hash, @tenant, @account, now() + interval '1 hour')",
            ("hash", new byte[] { 1 }), ("tenant", TenantA), ("account", account));
        await ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, guest_id, expires_at) values (@hash, @tenant, @guest, now() + interval '1 hour')",
            ("hash", new byte[] { 2 }), ("tenant", TenantA), ("guest", guestA));
        await AssertSqlStateAsync(() => ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, expires_at) values (@hash, @tenant, now() + interval '1 hour')", ("hash", new byte[] { 3 }), ("tenant", TenantA)), "23514");
        await AssertSqlStateAsync(() => ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, account_id, guest_id, expires_at) values (@hash, @tenant, @account, @guest, now() + interval '1 hour')", ("hash", new byte[] { 4 }), ("tenant", TenantA), ("account", account), ("guest", guestA)), "23514");
        await AssertSqlStateAsync(() => ExecuteAsync(system, "insert into auth_sessions (token_hash, tenant_id, guest_id, expires_at) values (@hash, @tenant, @guest, now() + interval '1 hour')", ("hash", new byte[] { 5 }), ("tenant", TenantA), ("guest", guestB)), "23503");
        Assert.False(await ScalarAsync<bool>(owner, "select exists (select 1 from information_schema.columns where table_name = 'auth_sessions' and column_name = 'token')"));

        await ExecuteAsync(system, "insert into login_codes (email, code_hash, expires_at) values ('login@example.test', @hash, now() + interval '10 minutes')", ("hash", new byte[] { 6 }));
        await ExecuteAsync(system, "update login_codes set attempts = attempts + 1 where email = 'login@example.test'");
        Assert.Equal((short)1, await ScalarAsync<short>(system, "select attempts from login_codes where email = 'login@example.test'"));
        await AssertSqlStateAsync(() => ExecuteAsync(system, "update login_codes set attempts = -1 where email = 'login@example.test'"), "23514");
        Assert.False(await ScalarAsync<bool>(owner, "select exists (select 1 from information_schema.columns where table_name = 'login_codes' and column_name = 'code')"));
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from auth_sessions; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from data_protection_keys; delete from tenants; delete from accounts");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'identity-a', 'Identity A', 'en'), (@b, 'identity-b', 'Identity B', 'en')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private async Task<NpgsqlConnection> PrepareBeforeIdentityMigrationAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from auth_sessions; delete from account_identities; delete from login_codes; delete from auth_rate_limits; delete from rate_limits; delete from data_protection_keys; delete from tenants; delete from accounts");
        await ExecuteAsync(owner, """
            drop function session_ended_effect_jobs(uuid);
            drop function due_sessions(timestamptz, int, uuid[]);
            drop function enqueue_job(text, text, jsonb, text, timestamptz, int);
            drop table session_effects;
            drop table plugin_effects;
            create function enqueue_job(job_owner text, job_kind text, job_payload jsonb,
                                        job_unique_key text default null, job_run_at timestamptz default now())
              returns void
              language sql security definer set search_path = public
              as $enqueue$
                insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at)
                values (app_tenant(), job_owner, job_kind, job_payload, job_unique_key, job_run_at)
                on conflict do nothing;
              $enqueue$;
            grant execute on function enqueue_job(text, text, jsonb, text, timestamptz) to quesshi_app, quesshi_system;
            drop function account_players(uuid);
            drop function claim_account(uuid);
            drop table account_identities;
            drop table login_codes;
            drop table auth_sessions;
            drop table auth_rate_limits;
            drop table rate_limits;
            drop table data_protection_keys;
            delete from schema_migrations where name = '20261013T0203_core_effects.sql';
            delete from schema_migrations where name = '20261013T0202_core_identity_security.sql';
            delete from schema_migrations where name = '20261014T0204_core_deadlines.sql';
            alter policy members_only on accounts
              using (exists (select 1 from players p
                            where p.account_id = accounts.id
                              and p.tenant_id = (select app_tenant())));
            """);
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'identity-a', 'Identity A', 'en'), (@b, 'identity-b', 'Identity B', 'en')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenant) =>
        await ExecuteAsync(connection, "select set_config('app.tenant_id', @tenant, false)", ("tenant", tenant.ToString()));

    private static async Task InsertPlayerAsync(NpgsqlConnection connection, Guid tenant, Guid player, Guid? account, string displayName = "Player", bool deleted = false) =>
        await ExecuteAsync(connection, "insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang, deleted_at) values (@tenant, @player, @account, @name, 'seed', 'en', case when @deleted then now() else null end)",
            ("tenant", tenant), ("player", player), ("account", account ?? (object)DBNull.Value), ("name", displayName), ("deleted", deleted));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        await ExecuteAsync(connection, sql, null, parameters);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
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
}
