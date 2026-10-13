using System.Text.Json;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class JobsDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("c98c3c18-1da8-4b9d-968a-4c42a66cc8ac");
    private static readonly Guid TenantB = Guid.Parse("6e2c713c-1827-4076-8e46-7b0a8698643c");

    [Fact]
    public void Core_migrations_include_the_jobs_contract_after_tenant_configuration()
    {
        var names = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly).Select(migration => migration.Name).ToArray();

        Assert.Contains("20261013T0101_core_jobs.sql", names);
        Assert.True(Array.IndexOf(names, "20261013T0100_core_tenant_configuration.sql") < Array.IndexOf(names, "20261013T0101_core_jobs.sql"));
    }

    [SkippableFact]
    public async Task Jobs_migration_creates_the_job_contract_and_restricts_direct_app_access()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        Assert.True(await ScalarAsync<bool>(owner, "select to_regclass('jobs') is not null"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from pg_indexes where tablename = 'jobs' and indexname = 'jobs_ready'"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from pg_indexes where tablename = 'jobs' and indexname = 'jobs_unique'"));

        await using var app = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_app"));
        await app.OpenAsync();
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "select * from jobs"));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "insert into jobs (kind) values ('forbidden')"));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "update jobs set kind = 'forbidden'"));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "delete from jobs"));

        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await ScalarAsync<long>(system, "select count(*) from jobs");
    }

    [SkippableFact]
    public async Task Tenant_and_system_enqueue_are_transactional_scoped_and_deduplicated()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var database = new TenantDb(sources);
        IJobs jobs = new TenantJobs();
        ISystemJobs systemJobs = new SystemJobs(sources);
        using var document = JsonDocument.Parse("""{"message":"queued"}""");
        var payload = document.RootElement.Clone();
        var runAt = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync<int>(TenantA, async tx =>
        {
            await jobs.EnqueueAsync(tx, "plugin", "rollback", payload);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(0L, await JobCountAsync("owner = 'plugin' and kind = 'rollback'"));

        await database.InTenantAsync(TenantA, async tx =>
        {
            await jobs.EnqueueAsync(tx, "plugin", "mail", payload, "retry", runAt);
            await jobs.EnqueueAsync(tx, "plugin", "mail", payload, "retry", runAt);
            await jobs.EnqueueAsync(tx, "core", "mail", payload, "retry");
            await jobs.EnqueueAsync(tx, "plugin", "other", payload, "retry");
            await jobs.EnqueueAsync(tx, "plugin", "mail", payload, "other");
            await jobs.EnqueueAsync(tx, "plugin", "mail", payload);
            await jobs.EnqueueAsync(tx, "plugin", "mail", payload);
            return 0;
        });
        await database.InTenantAsync(TenantB, async tx =>
        {
            await jobs.EnqueueAsync(tx, "plugin", "mail", payload, "retry");
            return 0;
        });

        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'mail' and unique_key = 'retry'", ("tenant", TenantA)));
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'mail' and unique_key = 'retry'", ("tenant", TenantB)));
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and owner = 'core' and kind = 'mail' and unique_key = 'retry'", ("tenant", TenantA)));
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'other' and unique_key = 'retry'", ("tenant", TenantA)));
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'mail' and unique_key = 'other'", ("tenant", TenantA)));
        Assert.Equal(2L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'mail' and unique_key is null", ("tenant", TenantA)));
        Assert.Equal(TenantA, await JobTenantAsync("owner = 'plugin' and kind = 'mail' and unique_key = 'retry'", ("tenant", TenantA)));
        Assert.Equal(runAt.UtcDateTime, await JobRunAtAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'mail' and unique_key = 'retry'", ("tenant", TenantA)));

        foreach (var status in new[] { "done", "cancelled", "dead" })
        {
            await database.InTenantAsync(TenantA, async tx =>
            {
                await jobs.EnqueueAsync(tx, "plugin", "finished", payload, status);
                return 0;
            });
            await SetStatusAsync(status, status);
            await database.InTenantAsync(TenantA, async tx =>
            {
                await jobs.EnqueueAsync(tx, "plugin", "finished", payload, status);
                return 0;
            });
            Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'finished' and unique_key = @key and status = 'ready'", ("tenant", TenantA), ("key", status)));
            Assert.Equal(2L, await JobCountAsync("tenant_id = @tenant and owner = 'plugin' and kind = 'finished' and unique_key = @key", ("tenant", TenantA), ("key", status)));
        }

        await systemJobs.EnqueueForTenantAsync(TenantB, "core", "tenant-system", payload, "system-tenant");
        await systemJobs.EnqueueGlobalAsync("core", "global-system", payload, "system-global");
        Assert.Equal(TenantB, await JobTenantAsync("owner = 'core' and kind = 'tenant-system'"));
        Assert.Null(await JobTenantAsync("owner = 'core' and kind = 'global-system'"));
    }

    [SkippableFact]
    public async Task App_enqueue_function_requires_a_bound_tenant_and_stamps_it_from_context()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var app = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_app"));
        await app.OpenAsync();

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, "select enqueue_job('plugin', 'unbound', '{}'::jsonb)"));

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ExecuteAsync(app, "select set_config('app.tenant_id', @tenant, true)", transaction, ("tenant", TenantA.ToString()));
            await ExecuteAsync(app, "select enqueue_job('plugin', 'stamped', @payload::jsonb)", transaction, ("payload", $"{{\"tenant_id\":\"{TenantB}\"}}"));
            await transaction.CommitAsync();
        }

        Assert.Equal(TenantA, await JobTenantAsync("owner = 'plugin' and kind = 'stamped'"));
    }

    private async Task PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from jobs");
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await ExecuteAsync(owner, """
                insert into tenants (id, slug, name, default_language)
                values (@tenant, @slug, @slug, 'en')
                on conflict (id) do nothing
                """, null, ("tenant", tenant), ("slug", $"jobs-{tenant:N}"));
        }
    }

    private async Task<long> JobCountAsync(string where, params (string Name, object Value)[] parameters)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await ScalarAsync<long>(owner, $"select count(*) from jobs where {where}", null, parameters);
    }

    private async Task<Guid?> JobTenantAsync(string where, params (string Name, object Value)[] parameters)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await ScalarAsync<Guid?>(owner, $"select tenant_id from jobs where {where}", null, parameters);
    }

    private async Task<DateTime> JobRunAtAsync(string where, params (string Name, object Value)[] parameters)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return await ScalarAsync<DateTime>(owner, $"select run_at from jobs where {where}", null, parameters);
    }

    private async Task SetStatusAsync(string status, string uniqueKey)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "update jobs set status = @status where tenant_id = @tenant and owner = 'plugin' and kind = 'finished' and unique_key = @key", null, ("status", status), ("tenant", TenantA), ("key", uniqueKey));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? default! : (T)result!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertInsufficientPrivilegeAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal("42501", exception.SqlState);
    }
}
