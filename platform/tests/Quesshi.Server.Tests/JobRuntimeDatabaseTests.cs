using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Server;
using Quesshi.Server.Health;
using Quesshi.Server.Jobs;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class JobRuntimeDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Concurrent_claims_do_not_share_a_valid_lease()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await InsertAsync("first");
        await InsertAsync("second");
        await using var sources = Sources();
        var first = new PostgresJobStore(sources);
        var second = new PostgresJobStore(sources);

        var claims = await Task.WhenAll(
            first.ClaimAsync(1, TimeSpan.FromMinutes(1)),
            second.ClaimAsync(1, TimeSpan.FromMinutes(1)));

        Assert.Equal(2, claims.SelectMany(rows => rows).Select(job => job.Id).Distinct().Count());
        Assert.All(claims.SelectMany(rows => rows), job => Assert.NotEqual(Guid.Empty, job.LockedBy));
    }

    [SkippableFact]
    public async Task A_stale_lease_cannot_change_a_new_owner_and_a_dead_job_can_be_rerun()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var id = await InsertAsync("bounded", maxAttempts: 2);
        await using var sources = Sources();
        var store = new PostgresJobStore(sources);
        var original = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));

        await using (var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system")))
        {
            await system.OpenAsync();
            await ExecuteAsync(system, "update jobs set locked_until = now() - interval '1 second' where id = @id", ("id", id));
        }
        var replacement = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.NotEqual(original.LockedBy, replacement.LockedBy);
        Assert.False(await store.CompleteAsync(id, original.LockedBy));
        Assert.False(await store.FailAsync(id, original.LockedBy, original.Attempts, original.MaxAttempts, "stale"));
        Assert.False(await store.CancelAsync(id, original.LockedBy));

        Assert.True(await store.FailAsync(id, replacement.LockedBy, replacement.Attempts, replacement.MaxAttempts, "bounded failure"));
        var dead = Assert.Single(await store.ListDeadAsync());
        Assert.Equal(id, dead.Id);
        Assert.Equal("bounded failure", dead.LastError);
        Assert.True(await store.RerunDeadAsync(id));
        var rerun = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.Equal(id, rerun.Id);
        Assert.True(await store.CompleteAsync(id, rerun.LockedBy));
    }

    [SkippableFact]
    public async Task An_expired_final_lease_is_parked_dead_without_being_claimed_again()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var id = await InsertAsync("exhausted", maxAttempts: 1);
        await using var sources = Sources();
        var store = new PostgresJobStore(sources);
        _ = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));

        await SqlAsync("update jobs set locked_until = now() - interval '1 second' where id = @id", ("id", id));

        Assert.Empty(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        var dead = Assert.Single(await store.ListDeadAsync());
        Assert.Equal(id, dead.Id);
        Assert.Equal(1, dead.Attempts);
    }

    [SkippableFact]
    public async Task Unlimited_jobs_remain_retryable_after_twenty_failures()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var id = await InsertAsync("unlimited", maxAttempts: null);
        await using var sources = Sources();
        var store = new PostgresJobStore(sources);

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var lease = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
            Assert.Equal(attempt, lease.Attempts);
            Assert.True(await store.FailAsync(id, lease.LockedBy, lease.Attempts, lease.MaxAttempts, $"failure {attempt}"));
            await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
            await system.OpenAsync();
            await ExecuteAsync(system, "update jobs set run_at = now() where id = @id", ("id", id));
        }

        Assert.Empty(await store.ListDeadAsync());
        var last = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.Equal(21, last.Attempts);
    }

    [SkippableFact]
    public async Task Failures_back_off_with_a_capped_delay()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var id = await InsertAsync("backoff", maxAttempts: null);
        await using var sources = Sources();
        var store = new PostgresJobStore(sources);

        var first = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.True(await store.FailAsync(id, first.LockedBy, first.Attempts, first.MaxAttempts, "one"));
        var firstDelay = await DelayAsync(id);
        await SqlAsync("update jobs set run_at = now() where id = @id", ("id", id));
        var second = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.True(await store.FailAsync(id, second.LockedBy, second.Attempts, second.MaxAttempts, "two"));
        Assert.True(await DelayAsync(id) > firstDelay);

        await SqlAsync("update jobs set run_at = now(), attempts = 40 where id = @id", ("id", id));
        var late = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.True(await store.FailAsync(id, late.LockedBy, late.Attempts, late.MaxAttempts, "late"));
        Assert.InRange(await DelayAsync(id), 290, 301);
    }

    [SkippableFact]
    public async Task Disabled_plugin_policy_cancels_or_completes_tenant_jobs_but_never_core_jobs()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var tenantId = await Program.AddTenantAsync([
            "--slug", $"jobs-{Guid.NewGuid():N}", "--name", "Jobs", "--host", $"{Guid.NewGuid():N}.example.test",
            "--default-language", "en", "--language", "en:English:English:ltr"], sources);
        var registry = new JobHandlerRegistry();
        var calls = new List<string>();
        registry.Register("plugin", "cancel", (context, _) => { lock (calls) calls.Add(context.Kind); return Task.CompletedTask; }, WhenDisabled.Cancel);
        registry.Register("plugin", "complete", (context, _) => { lock (calls) calls.Add(context.Kind); return Task.CompletedTask; }, WhenDisabled.Complete);
        registry.Register("core", "global", (context, _) =>
        {
            lock (calls) calls.Add(context.Kind);
            Assert.NotNull(context.Database);
            return Task.CompletedTask;
        }, WhenDisabled.Cancel);
        var cancelId = await InsertTenantJobAsync(tenantId, "plugin", "cancel");
        var completeId = await InsertTenantJobAsync(tenantId, "plugin", "complete");
        var coreId = await InsertTenantJobAsync(tenantId, "core", "global");
        var worker = new JobWorker(new PostgresJobStore(sources), registry, new TenantDb(sources), new OffCapabilities());

        Assert.Equal(3, await worker.RunPassAsync(10, TimeSpan.FromMinutes(1)));

        Assert.Equal(["complete", "global"], calls.Order().ToArray());
        Assert.Equal("cancelled", await StatusAsync(cancelId));
        Assert.Equal("done", await StatusAsync(completeId));
        Assert.Equal("done", await StatusAsync(coreId));
    }

    [SkippableFact]
    public async Task Tenant_handlers_only_reach_their_own_tenant_and_failures_are_recorded()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        await using var sources = Sources();
        var tenantId = await Program.AddTenantAsync([
            "--slug", $"jobs-{Guid.NewGuid():N}", "--name", "Jobs", "--host", $"{Guid.NewGuid():N}.example.test",
            "--default-language", "en", "--language", "en:English:English:ltr"], sources);
        var registry = new JobHandlerRegistry();
        var denied = false;
        registry.Register("core", "scoped", async (context, _) =>
        {
            Assert.Equal(tenantId, context.TenantId);
            await context.Database!.InTenantAsync(tenantId, _ => Task.FromResult(1));
            try { await context.Database.InTenantAsync(Guid.NewGuid(), _ => Task.FromResult(1)); }
            catch (InvalidOperationException) { denied = true; }
            throw new InvalidOperationException("boom");
        });
        var id = await InsertTenantJobAsync(tenantId, "core", "scoped");
        var worker = new JobWorker(new PostgresJobStore(sources), registry, new TenantDb(sources), new OffCapabilities());

        await worker.RunPassAsync(10, TimeSpan.FromMinutes(1));

        Assert.True(denied);
        Assert.Equal("ready", await StatusAsync(id));
        Assert.Contains("boom", await SingleAsync<string>("select last_error from jobs where id = @id", ("id", id)));
    }

    [Fact]
    public async Task The_host_registers_job_loops_unless_jobs_are_disabled()
    {
        var original = Environment.GetEnvironmentVariable("Jobs__Enabled");
        try
        {
            await using var sources = fixture.ConnectionString is null
                ? new DatabaseSources(
                    "Host=127.0.0.1;Database=quesshi;Username=quesshi_app",
                    "Host=127.0.0.1;Database=quesshi;Username=quesshi_system")
                : Sources();
            Environment.SetEnvironmentVariable("Jobs__Enabled", null);
            await using (var enabled = Program.CreateApplication(sources, 11127, 30127))
            {
                Assert.Contains(enabled.Services.GetServices<IHostedService>(), service => service.GetType().Name == "JobWorkerService");
                Assert.Contains(enabled.Services.GetServices<IHostedService>(), service => service.GetType().Name == "JobScheduleService");

                var features = enabled.Services.GetRequiredService<FeatureRegistry>();
                Assert.Same(features.Capabilities, enabled.Services.GetRequiredService<CapabilityRegistry>());
                Assert.Same(features.Jobs, enabled.Services.GetRequiredService<JobHandlerRegistry>());
                Assert.Same(features.Schedules, enabled.Services.GetRequiredService<JobScheduleRegistry>());
            }

            Environment.SetEnvironmentVariable("Jobs__Enabled", "false");
            await using var disabled = Program.CreateApplication(sources, 11128, 30128);
            Assert.DoesNotContain(disabled.Services.GetServices<IHostedService>(), service => service.GetType().Name == "JobWorkerService");
            Assert.DoesNotContain(disabled.Services.GetServices<IHostedService>(), service => service.GetType().Name == "JobScheduleService");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Jobs__Enabled", original);
        }
    }

    [Fact]
    public async Task A_silo_never_runs_more_than_four_handlers_at_once()
    {
        var registry = new JobHandlerRegistry();
        var active = 0;
        var peak = 0;
        registry.Register("core", "slow", async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(100);
            Interlocked.Decrement(ref active);
        });
        var store = new MemoryJobStore(Enumerable.Range(1, 12).Select(id => new LeasedJob(id, null, "core", "slow", default, Guid.NewGuid(), DateTimeOffset.UtcNow, 1, 10)));
        var worker = new JobWorker(store, registry, new UnusedTenantDb(), new OffCapabilities());

        while (store.Pending > 0) await worker.RunPassAsync(12, TimeSpan.FromMinutes(1));

        Assert.Equal(4, peak);
    }

    [Fact]
    public async Task Unlimited_core_and_plugin_cleanup_failures_are_logged_as_alerts()
    {
        var registry = new JobHandlerRegistry();
        registry.Register("core", "account-player-cleanup", (_, _) => throw new InvalidOperationException("core cleanup failed"));
        registry.Register("sample-plugin", "cleanup-player", (_, _) => throw new InvalidOperationException("plugin cleanup failed"));
        var tenant = Guid.NewGuid();
        var store = new MemoryJobStore([
            new LeasedJob(1, tenant, "core", "account-player-cleanup", default, Guid.NewGuid(), DateTimeOffset.UtcNow, 3, null),
            new LeasedJob(2, tenant, "sample-plugin", "cleanup-player", default, Guid.NewGuid(), DateTimeOffset.UtcNow, 4, null)
        ]);
        var logger = new RecordingJobLogger();
        var worker = new JobWorker(store, registry, new UnusedTenantDb(), new OffCapabilities(), logger);

        await worker.RunPassAsync(2, TimeSpan.FromMinutes(1));

        Assert.Contains(logger.Messages, message => message.Contains("core/account-player-cleanup", StringComparison.Ordinal) && message.Contains("attempt 3", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("sample-plugin/cleanup-player", StringComparison.Ordinal) && message.Contains("attempt 4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_shutdown_does_not_fault_when_a_handler_outlives_the_drain_timeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new JobHandlerRegistry();
        registry.Register("core", "slow", async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        });
        var store = new MemoryJobStore([new LeasedJob(1, null, "core", "slow", default, Guid.NewGuid(), DateTimeOffset.UtcNow, 1, null)]);
        var service = new JobWorkerService(
            new JobWorker(store, registry, new UnusedTenantDb(), new OffCapabilities()),
            new BackgroundLoopRegistry(),
            NullLogger<JobWorkerService>.Instance,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class MemoryJobStore(IEnumerable<LeasedJob> jobs) : IJobStore
    {
        private readonly Queue<LeasedJob> _pending = new(jobs);
        public int Pending => _pending.Count;
        public Task<IReadOnlyList<LeasedJob>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            var batch = new List<LeasedJob>();
            while (batch.Count < batchSize && _pending.TryDequeue(out var job)) batch.Add(job);
            return Task.FromResult<IReadOnlyList<LeasedJob>>(batch);
        }
        public Task<bool> CompleteAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> FailAsync(long id, Guid lockedBy, int attempts, int? maxAttempts, string error, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CancelAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<DeadJob>> ListDeadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeadJob>>([]);
        public Task<bool> RerunDeadAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingJobLogger : ILogger<JobWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                lock (Messages) Messages.Add(formatter(state, exception));
            }
        }
    }

    private sealed class UnusedTenantDb : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) =>
            throw new NotSupportedException();
    }

    private sealed class OffCapabilities : ICapabilities
    {
        public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
        public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
    }

    private async Task<long> InsertTenantJobAsync(Guid tenantId, string owner, string kind)
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await using var command = new NpgsqlCommand("insert into jobs (tenant_id, owner, kind) values (@tenant, @owner, @kind) returning id", system);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("kind", kind);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task<string> StatusAsync(long id) => SingleAsync<string>("select status from jobs where id = @id", ("id", id));

    private async Task<double> DelayAsync(long id) =>
        await SingleAsync<double>("select extract(epoch from run_at - now())::float8 from jobs where id = @id", ("id", id));

    private async Task<T> SingleAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await using var command = new NpgsqlCommand(sql, system);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await ExecuteAsync(system, sql, parameters);
    }

    private async Task PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from jobs");
    }

    private async Task<long> InsertAsync(string kind, int? maxAttempts = 10)
    {
        await using var system = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await system.OpenAsync();
        await using var command = new NpgsqlCommand("""
            insert into jobs (owner, kind, payload, max_attempts)
            values ('core', @kind, '{}'::jsonb, @maxAttempts)
            returning id
            """, system);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("maxAttempts", maxAttempts is null ? DBNull.Value : maxAttempts.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
