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

        try
        {
            var discovered = MigrationRunner.Discover(typeof(DatabaseFoundationTests).Assembly);
            Assert.Collection(discovered,
                migration => Assert.Equal("20261013T0205_ai_generation_before.sql", migration.Name),
                migration => Assert.Equal("20261013T0210_core_after.sql", migration.Name));

            var scripts = CoreScripts().Concat(discovered).ToArray();
            await RunAsync(scripts);
            await RunAsync(scripts);
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations where name = '20261013T0205_ai_generation_before.sql' and owner = 'ai_generation'"));
            Assert.Equal(8L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations"));

            var changed = new MigrationScript("20261013T0205_ai_generation_before.sql", "ai_generation", "create table changed_bytes (id int);"u8.ToArray());
            var later = Script("20261013T0220_core_later", "create table runner_later (id integer primary key);");
            var checksumFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync([.. CoreScripts(), changed, discovered[1], later]));
            Assert.Contains(changed.Name, checksumFailure.Message);
            Assert.False(await ScalarAsync<bool>(owner, "select to_regclass('runner_later') is not null"));

            var earlier = Script("20261013T0206_core_too_early", "create table runner_too_early (id integer primary key);");
            var orderFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync([.. scripts, earlier]));
            Assert.Contains(earlier.Name, orderFailure.Message);
            Assert.Contains("20261013T0210_core_after.sql", orderFailure.Message);

            var rollback = Script("20261013T0215_core_rollback", "create table runner_rollback (id integer primary key); select missing_function();");
            await Assert.ThrowsAsync<PostgresException>(() => RunAsync([.. scripts, rollback]));
            Assert.False(await ScalarAsync<bool>(owner, "select to_regclass('runner_rollback') is not null"));
            Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations where name = '20261013T0215_core_rollback.sql'"));

            var delayed = Script("20261013T0225_core_concurrent", "select pg_sleep(1); create table runner_concurrent (id integer primary key);");
            var concurrentScripts = scripts.Append(delayed).ToArray();
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            var first = RunAsync(concurrentScripts);
            await WaitUntilAsync(
                () => ScalarAsync<bool>(admin, "select exists (select 1 from pg_stat_activity where usename = 'quesshi_owner' and state = 'active' and query like '%pg_sleep(1)%')"),
                "the first migration run did not begin its delayed migration");
            var second = RunAsync(concurrentScripts);
            await WaitUntilAsync(
                () => ScalarAsync<bool>(admin, "select exists (select 1 from pg_stat_activity where usename = 'quesshi_owner' and wait_event_type = 'Lock' and wait_event = 'advisory')"),
                "the second migration run did not wait for the advisory lock");
            await Task.WhenAll(first, second);
            Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from schema_migrations where name = '20261013T0225_core_concurrent.sql'"));

            await ExecuteAsync(admin, "create schema runner_shadow");
            await ExecuteAsync(admin, "create table runner_shadow.schema_migrations (name text primary key, owner text not null, checksum text not null, applied_at timestamptz not null default now())");
            await ExecuteAsync(admin, "grant usage, create on schema runner_shadow to quesshi_owner");
            await ExecuteAsync(admin, "grant select, insert on runner_shadow.schema_migrations to quesshi_owner");
            foreach (var script in concurrentScripts)
            {
                await RecordMigrationAsync(admin, "runner_shadow.schema_migrations", script);
            }

            var pathMigration = Script("20261013T0230_core_search_path", "create table runner_public_path (id integer primary key);");
            await using (var customPath = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionStringForRole("quesshi_owner"))
            {
                SearchPath = "runner_shadow, public"
            }.ConnectionString))
            {
                await customPath.OpenAsync();
                await new MigrationRunner(concurrentScripts.Append(pathMigration)).RunAsync(customPath);
            }
            Assert.True(await ScalarAsync<bool>(admin, "select to_regclass('public.runner_public_path') is not null"));
            Assert.Equal(1L, await ScalarAsync<long>(admin, "select count(*) from public.schema_migrations where name = '20261013T0230_core_search_path.sql'"));
            Assert.Equal(0L, await ScalarAsync<long>(admin, "select count(*) from runner_shadow.schema_migrations where name = '20261013T0230_core_search_path.sql'"));

            var cancellationMigration = Script("20261013T0235_core_cancellation", "select pg_sleep(5);");
            await using var callerConnection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await callerConnection.OpenAsync();
            using var cancellation = new CancellationTokenSource();
            var cancelledRun = new MigrationRunner(concurrentScripts.Append(pathMigration).Append(cancellationMigration)).RunAsync(callerConnection, cancellation.Token);
            await WaitUntilAsync(
                () => ScalarAsync<bool>(admin, "select exists (select 1 from pg_stat_activity where usename = 'quesshi_owner' and state = 'active' and query like '%pg_sleep(5)%')"),
                "the cancellation migration did not begin its delayed statement");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun);

            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await using var verifyingConnection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await new MigrationRunner(concurrentScripts.Append(pathMigration)).RunAsync(verifyingConnection, releaseTimeout.Token);
        }
        finally
        {
            await RemoveRunnerArtifactsAsync();
        }
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
        Assert.Equal("quesshi_owner", await ScalarAsync<string>(admin, "select pg_get_userbyid(proowner) from pg_proc where oid = 'app_tenant()'::regprocedure"));
        Assert.Equal("quesshi_owner", await ScalarAsync<string>(admin, "select pg_get_userbyid(proowner) from pg_proc where oid = 'quesshi_tenant_table(regclass, text)'::regprocedure"));

        foreach (var role in new[] { "quesshi_app", "quesshi_system" })
        {
            await using var runtime = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
            await runtime.OpenAsync();
            await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(runtime, $"create table {role}_must_not_create (id integer)"));
            if (role == "quesshi_app")
            {
                await AssertInsufficientPrivilegeAsync(async () => await ScalarAsync<long>(runtime, "select count(*) from schema_migrations"));
            }
            else
            {
                Assert.True(await ScalarAsync<long>(runtime, "select count(*) from schema_migrations") >= 4L);
            }
            await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(runtime, "select quesshi_tenant_table('schema_migrations', 'demo')"));
            if (role == "quesshi_app")
            {
                await ExecuteAsync(runtime, "select set_config('app.tenant_id', '3f7ca0e3-8907-4fb0-ae58-3584b63ea667', false)");
                Assert.Equal(Guid.Parse("3f7ca0e3-8907-4fb0-ae58-3584b63ea667"), await ScalarAsync<Guid>(runtime, "select app_tenant()"));
            }
            else
            {
                await AssertInsufficientPrivilegeAsync(async () => await ScalarAsync<Guid>(runtime, "select app_tenant()"));
            }
        }
    }

    private async Task ApplyCoreAsync() => await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

    private async Task RemoveRunnerArtifactsAsync()
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, """
            drop schema if exists runner_shadow cascade;
            drop table if exists runner_ai_generation_before, runner_core_after, runner_concurrent, runner_public_path;
            delete from schema_migrations
            where name in (
                '20261013T0205_ai_generation_before.sql',
                '20261013T0210_core_after.sql',
                '20261013T0225_core_concurrent.sql',
                '20261013T0230_core_search_path.sql');
            """);
    }

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

    private static async Task RecordMigrationAsync(NpgsqlConnection connection, string table, MigrationScript script)
    {
        await using var command = new NpgsqlCommand($"insert into {table} (name, owner, checksum) values (@name, @owner, @checksum)", connection);
        command.Parameters.AddWithValue("name", script.Name);
        command.Parameters.AddWithValue("owner", script.Owner);
        command.Parameters.AddWithValue("checksum", script.Checksum);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertInsufficientPrivilegeAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal("42501", exception.SqlState);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failureMessage)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail(failureMessage);
    }
}
