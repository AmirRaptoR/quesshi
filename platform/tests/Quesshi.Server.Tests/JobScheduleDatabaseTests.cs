using Npgsql;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class JobScheduleDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("83796c00-c6c8-4e09-90e5-38931f3f2a75");
    private static readonly Guid TenantB = Guid.Parse("b3bc44cf-7628-4b32-b5ef-a5a098a64024");
    private static readonly Guid DisabledTenant = Guid.Parse("b95323e8-147f-421d-bc20-45da51a1a6d4");

    [SkippableFact]
    public async Task Concurrent_silos_schedule_each_enabled_tenant_once_per_utc_window()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-13T10:01:30Z"));
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));

        var first = CreateScheduler(sources, time);
        var second = CreateScheduler(sources, time);
        await Task.WhenAll(first.RunPassAsync(), second.RunPassAsync());

        var firstWindow = JobScheduler.WindowStart(time.GetUtcNow(), TimeSpan.FromMinutes(1));
        Assert.Equal(2L, await JobCountAsync());
        Assert.Equal(2L, await JobCountAsync("tenant_id in (@a, @b)", ("a", TenantA), ("b", TenantB)));
        Assert.Equal(0L, await JobCountAsync("tenant_id = @tenant", ("tenant", DisabledTenant)));
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant and run_at = @window", ("tenant", TenantA), ("window", firstWindow)));

        time.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(first.RunPassAsync(), second.RunPassAsync());

        Assert.Equal(4L, await JobCountAsync());
        Assert.Equal(2L, await JobCountAsync("tenant_id = @tenant", ("tenant", TenantA)));
        Assert.Equal(2L, await JobCountAsync("tenant_id = @tenant", ("tenant", TenantB)));
        Assert.Equal(2L, await DistinctKeyCountAsync());
    }

    [SkippableFact]
    public async Task Completed_scheduled_jobs_are_not_reenqueued_in_their_window()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-13T10:01:30Z"));
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));

        var first = CreateScheduler(sources, time);
        var second = CreateScheduler(sources, time);
        await first.RunPassAsync();
        await SetScheduledJobsStatusAsync("done");
        await Task.WhenAll(first.RunPassAsync(), second.RunPassAsync());

        Assert.Equal(2L, await JobCountAsync());
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant", ("tenant", TenantA)));
        Assert.Equal(1L, await JobCountAsync("tenant_id = @tenant", ("tenant", TenantB)));
        Assert.Equal(2L, await ScheduleWindowCountAsync());
    }

    private static JobScheduler CreateScheduler(DatabaseSources sources, TimeProvider time)
    {
        var handlers = new JobHandlerRegistry();
        handlers.Register("plugin", "sweep", (_, _) => Task.CompletedTask);
        var schedules = new JobScheduleRegistry(handlers);
        schedules.Every("plugin", TimeSpan.FromMinutes(1), "sweep");
        return new JobScheduler(schedules, new PostgresJobScheduleTenants(sources), new SystemJobs(sources), time);
    }

    private async Task PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from jobs; delete from job_schedule_windows; delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        foreach (var (tenant, enabled) in new[] { (TenantA, true), (TenantB, true), (DisabledTenant, false) })
        {
            await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@tenant, @slug, @slug, 'en')", ("tenant", tenant), ("slug", tenant.ToString("N")));
            await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
            await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled) values (@tenant, 'feature', 'plugin', true, @enabled)", ("tenant", tenant), ("enabled", enabled));
        }
    }

    private async Task<long> JobCountAsync(string? where = null, params (string Name, object Value)[] parameters)
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await using var command = new NpgsqlCommand($"select count(*) from jobs{(where is null ? "" : " where " + where)}", system);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> DistinctKeyCountAsync()
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await using var command = new NpgsqlCommand("select count(distinct unique_key) from jobs", system);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ScheduleWindowCountAsync()
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from job_schedule_windows", system);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task SetScheduledJobsStatusAsync(string status)
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await ExecuteAsync(system, "update jobs set status = @status where owner = 'plugin' and kind = 'sweep'", ("status", status));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
