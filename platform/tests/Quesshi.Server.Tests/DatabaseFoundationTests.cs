using System.Diagnostics;
using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class DatabaseFoundationTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Core_migration_creates_the_isolation_primitives_and_tenant_helper()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await ApplyCoreAsync();

        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await using (var extensions = new NpgsqlCommand("select array_agg(extname order by extname) from pg_extension where extname in ('citext', 'pg_trgm')", owner))
        {
            var names = (string[])(await extensions.ExecuteScalarAsync())!;
            Assert.Equal(["citext", "pg_trgm"], names);
        }

        await using (var tables = new NpgsqlCommand("select tablename from pg_tables where schemaname = 'public' order by tablename", owner))
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            Assert.Contains("schema_migrations", names);
        }

        await Assert.ThrowsAsync<PostgresException>(async () => await ScalarAsync<Guid>(owner, "select app_tenant()"));
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await using var config = new NpgsqlCommand("select set_config('app.tenant_id', @tenant, true)", owner, transaction);
            config.Parameters.AddWithValue("tenant", "3f7ca0e3-8907-4fb0-ae58-3584b63ea667");
            await config.ExecuteNonQueryAsync();
            await using var tenant = new NpgsqlCommand("select app_tenant()", owner, transaction);
            Assert.Equal(Guid.Parse("3f7ca0e3-8907-4fb0-ae58-3584b63ea667"), (Guid)(await tenant.ExecuteScalarAsync())!);
            await transaction.RollbackAsync();
        }

        await ExecuteAsync(owner, "create table no_tenant_key (id uuid primary key)");
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "select quesshi_tenant_table('no_tenant_key', 'demo')"));
        Assert.Contains("primary key starting with tenant_id", invalid.MessageText);

        await ExecuteAsync(owner, "create table tenant_registered (tenant_id uuid not null, id uuid not null, primary key (tenant_id, id))");
        await ExecuteAsync(owner, "select quesshi_tenant_table('tenant_registered', 'demo')");
        Assert.Equal("owner:demo", await ScalarAsync<string>(owner, "select obj_description('tenant_registered'::regclass)"));
        Assert.True(await ScalarAsync<bool>(owner, "select relrowsecurity from pg_class where oid = 'tenant_registered'::regclass"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from pg_policies where tablename = 'tenant_registered' and policyname = 'tenant_isolation'"));
        Assert.True(await ScalarAsync<bool>(owner, "select has_table_privilege('quesshi_app', 'tenant_registered', 'select')"));
        Assert.False(await ScalarAsync<bool>(owner, "select has_table_privilege('quesshi_system', 'tenant_registered', 'select')"));
    }

    [SkippableFact]
    public async Task Runner_orders_resources_and_guards_its_ledger_and_lock()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await ApplyCoreAsync();
        var discovered = MigrationRunner.Discover(typeof(DatabaseFoundationTests).Assembly);
        Assert.Collection(discovered,
            migration => Assert.Equal("20261012T1205_ai_generation_before.sql", migration.Name),
            migration => Assert.Equal("20261012T1210_core_after.sql", migration.Name));

        var scripts = CoreScripts().Concat(discovered).ToArray();
        await RunAsync(scripts);
        await RunAsync(scripts);
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations where name = '20261012T1205_ai_generation_before.sql' and owner = 'ai_generation'"));
        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations"));

        var changed = new MigrationScript("20261012T1205_ai_generation_before.sql", "ai_generation", "create table changed_bytes (id int);"u8.ToArray());
        var later = Script("20261012T1220_core_later", "create table runner_later (id integer primary key);");
        var checksumFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync([.. CoreScripts(), changed, discovered[1], later]));
        Assert.Contains(changed.Name, checksumFailure.Message);
        Assert.False(await ScalarAsync<bool>(owner, "select to_regclass('runner_later') is not null"));

        var earlier = Script("20261012T1206_core_too_early", "create table runner_too_early (id integer primary key);");
        var orderFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync([.. scripts, earlier]));
        Assert.Contains(earlier.Name, orderFailure.Message);
        Assert.Contains("20261012T1210_core_after.sql", orderFailure.Message);

        var rollback = Script("20261012T1215_core_rollback", "create table runner_rollback (id integer primary key); select missing_function();");
        await Assert.ThrowsAsync<PostgresException>(() => RunAsync([.. scripts, rollback]));
        Assert.False(await ScalarAsync<bool>(owner, "select to_regclass('runner_rollback') is not null"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations where name = '20261012T1215_core_rollback.sql'"));

        var delayed = Script("20261012T1225_core_concurrent", "select pg_sleep(1); create table runner_concurrent (id integer primary key);");
        var concurrentScripts = scripts.Append(delayed).ToArray();
        var clock = Stopwatch.StartNew();
        var first = RunAsync(concurrentScripts);
        await Task.Delay(100);
        var second = RunAsync(concurrentScripts);
        await Task.WhenAll(first, second);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(800));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations where name = '20261012T1225_core_concurrent.sql'"));
    }

    [SkippableFact]
    public async Task Runtime_roles_have_no_elevated_or_owner_access()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await ApplyCoreAsync();
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var roles = new NpgsqlCommand("select rolname, rolsuper, rolbypassrls from pg_roles where rolname in ('quesshi_owner', 'quesshi_app', 'quesshi_system')", admin))
        await using (var reader = await roles.ExecuteReaderAsync())
        {
            var count = 0;
            while (await reader.ReadAsync())
            {
                count++;
                Assert.False(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
            }
            Assert.Equal(3, count);
        }
        Assert.Equal(0L, await ScalarAsync<long>(admin, "select count(*) from pg_auth_members m join pg_roles parent on parent.oid = m.roleid join pg_roles member on member.oid = m.member where parent.rolname like 'quesshi_%' and member.rolname like 'quesshi_%'"));
        Assert.Equal("quesshi_owner", await ScalarAsync<string>(admin, "select tableowner from pg_tables where schemaname = 'public' and tablename = 'schema_migrations'"));

        foreach (var role in new[] { "quesshi_app", "quesshi_system" })
        {
            await using var runtime = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
            await runtime.OpenAsync();
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(runtime, $"create table {role}_must_not_create (id integer)"));
            await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<long>(runtime, "select count(*) from schema_migrations"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(runtime, "select quesshi_tenant_table('schema_migrations', 'demo')"));
        }
    }

    private async Task ApplyCoreAsync() => await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

    private static IReadOnlyList<MigrationScript> CoreScripts() => MigrationRunner.Discover(typeof(ApplicationMarker).Assembly);

    private async Task RunAsync(IEnumerable<MigrationScript> scripts)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await new MigrationRunner(scripts).RunAsync(connection);
    }

    private static MigrationScript Script(string stem, string sql) => new($"{stem}.sql", "core", System.Text.Encoding.UTF8.GetBytes(sql));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
