using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

/// <summary>Exercises durable effects through the PostgreSQL job store and tenant transaction boundary.</summary>
[Collection("PostgreSQL")]
public sealed class DurableEffectsDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Emission_is_transactional_and_feature_state_is_persisted_with_the_job()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, session);
        await EnsureTenantAsync(otherTenant);
        var feature = new SampleFeature("sample");
        var retired = new SampleFeature("retired", retired: true);
        var registry = FeatureRegistry.Create([feature, retired]);
        var notifications = new RecordingNotifier();
        var events = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), notifications);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, feature.Id, enabled: false);

        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });
        Assert.Equal(0, await CountJobsAsync(tenant));

        await SetCapabilityAsync(tenant, feature.Id, enabled: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTenantAsync<int>(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            throw new InvalidOperationException("rollback event jobs");
        }));
        Assert.Equal(0, await CountJobsAsync(tenant));

        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });
        Assert.Equal(1, await CountJobsAsync(tenant));
        Assert.Equal("pending", Assert.Single((await ReadStateAsync(sources, events, tenant, session)).Features, state => state.FeatureId == feature.Id).Status);
        Assert.Equal("off", Assert.Single((await ReadStateAsync(sources, events, tenant, session)).Features, state => state.FeatureId == retired.Id).Status);

        await SetCapabilityAsync(tenant, feature.Id, enabled: false);
        await RunWorkerAsync(sources, registry, events, notifications);
        Assert.Equal(1, await CountMarkersAsync(tenant, feature.Id, $"sessionEnded:{session}"));
        Assert.Equal("done", Assert.Single((await ReadStateAsync(sources, events, tenant, session)).Features, state => state.FeatureId == feature.Id).Status);

        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });
        await SetCapabilityAsync(tenant, feature.Id, enabled: true);
        Assert.Equal(1, await CountJobsAsync(tenant));
        var hidden = await ReadStateAsync(sources, events, otherTenant, session);
        Assert.All(hidden.Features, state => Assert.Equal("off", state.Status));
    }

    [SkippableFact]
    public async Task Failure_dead_rerun_and_overlapping_workers_are_exactly_once_and_notify_revisions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, session);
        var feature = new SampleFeature("retry", eventFailures: 1);
        var registry = FeatureRegistry.Create([feature]);
        var notifications = new RecordingNotifier();
        var events = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), notifications);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, feature.Id, enabled: true);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });
        var jobId = await SingleJobIdAsync(tenant);
        await ExecuteSystemAsync("update jobs set max_attempts = 1 where id = @jobId", ("jobId", jobId));

        await RunWorkerAsync(sources, registry, events, notifications);
        Assert.Equal("dead", await JobStatusAsync(jobId));
        Assert.Equal(0, await CountMarkersAsync(tenant, feature.Id, $"sessionEnded:{session}"));
        Assert.Equal(0, await CountMarkersAsync(tenant, $"effect:{feature.Id}", $"session:{session}"));
        Assert.Equal(1L, await RevisionAsync(tenant, session));
        Assert.Equal([session], notifications.Sessions);
        Assert.Equal("failed", Assert.Single((await ReadStateAsync(sources, events, tenant, session)).Features).Status);

        var store = new PostgresJobStore(sources, notifications);
        Assert.True(await store.RerunDeadAsync(jobId));
        Assert.Equal(2L, await RevisionAsync(tenant, session));
        Assert.Equal([session, session], notifications.Sessions);
        await RunWorkerAsync(sources, registry, events, notifications);
        Assert.Equal("done", await JobStatusAsync(jobId));
        Assert.Equal(1, await CountMarkersAsync(tenant, $"effect:{feature.Id}", $"session:{session}"));
        Assert.Equal(3L, await RevisionAsync(tenant, session));
        Assert.Equal(3, notifications.Sessions.Count(id => id == session));

        var concurrentSession = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, concurrentSession);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(concurrentSession));
            return 0;
        });
        await Task.WhenAll(
            RunWorkerAsync(sources, registry, events, notifications),
            RunWorkerAsync(sources, registry, events, notifications));
        Assert.Equal(1, await CountMarkersAsync(tenant, $"effect:{feature.Id}", $"session:{concurrentSession}"));

        var expiredSession = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, expiredSession);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(expiredSession));
            return 0;
        });
        var expiredJob = await JobIdForSessionAsync(tenant, expiredSession);
        _ = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        await ExecuteSystemAsync("update jobs set locked_until = now() - interval '1 second', max_attempts = 1 where id = @jobId", ("jobId", expiredJob));
        Assert.Empty(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.Equal("dead", await JobStatusAsync(expiredJob));
        Assert.Equal(1L, await RevisionAsync(tenant, expiredSession));
        Assert.Contains(expiredSession, notifications.Sessions);
    }

    [SkippableFact]
    public async Task Simultaneous_expired_handlers_for_one_session_are_parked_and_revised_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, session);
        var first = new SampleFeature("expired-first");
        var second = new SampleFeature("expired-second");
        var registry = FeatureRegistry.Create([first, second]);
        var notifications = new RecordingNotifier();
        var events = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), notifications);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, first.Id, enabled: true);
        await SetCapabilityAsync(tenant, second.Id, enabled: true);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });

        var store = new PostgresJobStore(sources, notifications);
        Assert.Equal(2, (await store.ClaimAsync(10, TimeSpan.FromMinutes(1))).Count);
        await ExecuteSystemAsync("""
            update jobs set locked_until = now() - interval '1 second', max_attempts = 1
            where tenant_id = @tenant and kind = 'event' and payload->>'subjectId' = @session
            """, ("tenant", tenant), ("session", session.ToString()));

        Assert.Empty(await store.ClaimAsync(10, TimeSpan.FromMinutes(1)));
        Assert.Equal(2L, await ScalarSystemAsync<long>("""
            select count(*) from jobs where tenant_id = @tenant and kind = 'event' and status = 'dead'
            """, ("tenant", tenant)));
        Assert.Equal(2L, await RevisionAsync(tenant, session));
        Assert.Equal(1, notifications.Sessions.Count(id => id == session));
    }

    [SkippableFact]
    public async Task Session_effects_converge_when_events_are_delivered_in_the_reverse_order()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, firstSession);
        await CreateSessionAsync(tenant, player, secondSession);
        var feature = new SampleFeature("convergent", convergent: true);
        var registry = FeatureRegistry.Create([feature]);
        var events = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new RecordingNotifier());
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, feature.Id, enabled: true);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(firstSession));
            await events.EmitAsync(transaction, new SessionEnded(secondSession));
            return 0;
        });
        await ExecuteSystemAsync("update jobs set run_at = now() + interval '1 minute' where id = @jobId", ("jobId", await JobIdForSessionAsync(tenant, firstSession)));

        await RunWorkerAsync(sources, registry, events, new RecordingNotifier());
        await ExecuteSystemAsync("update jobs set run_at = now() where id = @jobId", ("jobId", await JobIdForSessionAsync(tenant, firstSession)));
        await RunWorkerAsync(sources, registry, events, new RecordingNotifier());

        Assert.Equal(1, await CountMarkersAsync(tenant, $"effect:{feature.Id}", $"player:{player}"));
    }

    [SkippableFact]
    public async Task Event_queued_while_enabled_runs_after_the_feature_is_retired()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, session);
        var enabledFeature = new SampleFeature("retired-after-emission");
        var initialRegistry = FeatureRegistry.Create([enabledFeature]);
        var initialEvents = new DurableEvents(initialRegistry, new TenantJobs(), new TenantLocks(), new RecordingNotifier());
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, enabledFeature.Id, enabled: true);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await initialEvents.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });

        var retiredFeature = new SampleFeature(enabledFeature.Id, retired: true);
        var retiredRegistry = FeatureRegistry.Create([retiredFeature]);
        var retiredEvents = new DurableEvents(retiredRegistry, new TenantJobs(), new TenantLocks(), new RecordingNotifier());
        await RunWorkerAsync(sources, retiredRegistry, retiredEvents, new RecordingNotifier());

        Assert.Equal(1, await CountMarkersAsync(tenant, retiredFeature.Id, $"sessionEnded:{session}"));
        Assert.Equal(1, await CountMarkersAsync(tenant, $"effect:{retiredFeature.Id}", $"session:{session}"));
    }

    [SkippableFact]
    public async Task Failed_feature_does_not_change_another_effect_or_the_committed_session_result()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        await CreateSessionAsync(tenant, player, session);
        var succeeds = new SampleFeature("succeeds");
        var fails = new SampleFeature("fails", eventFailures: 1);
        var registry = FeatureRegistry.Create([succeeds, fails]);
        var events = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new RecordingNotifier());
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, succeeds.Id, enabled: true);
        await SetCapabilityAsync(tenant, fails.Id, enabled: true);
        var committedResult = await SessionResultAsync(tenant, session);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });
        var failedJob = await JobIdForFeatureAsync(tenant, fails.Id);
        await ExecuteSystemAsync("update jobs set max_attempts = 1 where id = @jobId", ("jobId", failedJob));

        await RunWorkerAsync(sources, registry, events, new RecordingNotifier());
        Assert.Equal("done", await JobStatusForFeatureAsync(tenant, succeeds.Id));
        Assert.Equal("dead", await JobStatusAsync(failedJob));
        Assert.Equal(1, await CountMarkersAsync(tenant, $"effect:{succeeds.Id}", $"session:{session}"));
        Assert.Equal(0, await CountMarkersAsync(tenant, $"effect:{fails.Id}", $"session:{session}"));
        Assert.Equal(committedResult, await SessionResultAsync(tenant, session));

        var store = new PostgresJobStore(sources);
        Assert.True(await store.RerunDeadAsync(failedJob));
        await RunWorkerAsync(sources, registry, events, new RecordingNotifier());
        Assert.Equal(1, await CountMarkersAsync(tenant, $"effect:{fails.Id}", $"session:{session}"));
        Assert.Equal(committedResult, await SessionResultAsync(tenant, session));
    }

    [SkippableFact]
    public async Task Deleted_players_skip_event_data_and_cleanup_runs_independently_with_unlimited_retries()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await PrepareAsync();
        var tenant = Guid.NewGuid();
        var player = Guid.NewGuid();
        await CreatePlayerAsync(tenant, player);
        var eventFeature = new SampleFeature("upgrade", cleanup: false);
        var cleanupFails = new SampleFeature("cleanup-fails", cleanupFailures: 1);
        var cleanupWorks = new SampleFeature("cleanup-works");
        var retiredCleanup = new SampleFeature("cleanup-retired", retired: true);
        var registry = FeatureRegistry.Create([eventFeature, cleanupFails, cleanupWorks, retiredCleanup]);
        var locks = new RecordingLocks();
        var events = new DurableEvents(registry, new TenantJobs(), locks, new RecordingNotifier());
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await SetCapabilityAsync(tenant, eventFeature.Id, enabled: true);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new PlayerUpgraded(player));
            await events.EnqueueAsync(transaction, player);
            return 0;
        });
        Assert.Equal(1, await CountJobsAsync(tenant));

        await database.InTenantAsync(tenant, transaction => transaction.ExecuteAsync(
            "update players set deleted_at = now() where id = @player",
            new { player }));
        await database.InTenantAsync(tenant, async transaction =>
        {
            await events.EnqueueAsync(transaction, player);
            return 0;
        });
        Assert.Equal(3, await CountCleanupJobsAsync(tenant));
        Assert.Equal(3, await ScalarSystemAsync<long>("select count(*) from jobs where tenant_id = @tenant and kind = 'cleanup-player' and max_attempts is null", ("tenant", tenant)));

        await RunWorkerAsync(sources, registry, events, new RecordingNotifier());
        Assert.Equal(1, await CountMarkersAsync(tenant, $"cleanup:{cleanupWorks.Id}", $"player:{player}"));
        Assert.Equal(1, await CountMarkersAsync(tenant, $"cleanup:{retiredCleanup.Id}", $"player:{player}"));
        Assert.Equal(0, await CountMarkersAsync(tenant, $"cleanup:{cleanupFails.Id}", $"player:{player}"));
        Assert.Equal(0, await CountMarkersAsync(tenant, $"effect:{eventFeature.Id}", $"player:{player}"));

        await ExecuteSystemAsync("update jobs set run_at = now() where tenant_id = @tenant and kind = 'cleanup-player' and status = 'ready'", ("tenant", tenant));
        await RunWorkerAsync(sources, registry, events, new RecordingNotifier());
        Assert.Equal(1, await CountMarkersAsync(tenant, $"cleanup:{cleanupFails.Id}", $"player:{player}"));
        Assert.True(locks.PlayerCalls >= 4);
    }

    private async Task PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await ExecuteSystemAsync("delete from jobs");
    }

    private DatabaseSources Sources() => new(
        fixture.ConnectionStringForRole("quesshi_app"),
        fixture.ConnectionStringForRole("quesshi_system"));

    private async Task RunWorkerAsync(DatabaseSources sources, FeatureRegistry registry, DurableEvents events, ISessionEffectsNotifier notifications)
    {
        var handlers = registry.Jobs;
        if (!handlers.TryGet("core", "event", out _)) handlers.Register("core", "event", events.HandleEventJobAsync);
        if (!handlers.TryGet("core", "cleanup-player", out _)) handlers.Register("core", "cleanup-player", events.HandleCleanupJobAsync);
        var worker = new JobWorker(new PostgresJobStore(sources, notifications), handlers, new TenantDb(sources), new OffCapabilities());
        await worker.RunPassAsync(10, TimeSpan.FromMinutes(1));
    }

    private async Task<SessionEffectsState> ReadStateAsync(DatabaseSources sources, DurableEvents events, Guid tenant, Guid session) =>
        await new TenantDb(sources).InTenantAsync(tenant, transaction => events.GetAsync(transaction, session));

    private async Task EnsureTenantAsync(Guid tenant) =>
        await ExecuteSystemAsync("""
            insert into tenants (id, slug, name, default_language)
            values (@tenant, @slug, @slug, 'en')
            on conflict (id) do nothing
            """, ("tenant", tenant), ("slug", $"effects-{tenant:N}"));

    private async Task CreatePlayerAsync(Guid tenant, Guid player)
    {
        await EnsureTenantAsync(tenant);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await database.InTenantAsync(tenant, transaction => transaction.ExecuteAsync("""
            insert into players (tenant_id, id, display_name, avatar_seed, lang)
            values (@tenant, @player, 'Effects', 'effects', 'en')
            on conflict (tenant_id, id) do nothing
            """, new { tenant, player }));
    }

    private async Task CreateSessionAsync(Guid tenant, Guid player, Guid session)
    {
        await CreatePlayerAsync(tenant, player);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await database.InTenantAsync(tenant, transaction => transaction.ExecuteAsync("""
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at)
            values (@tenant, @session, @code, 'test', @player, 'en', 'finished', 1, '{}'::jsonb, 'finished', now(), now());
            insert into session_participants (tenant_id, session_id, player_id, seat)
            values (@tenant, @session, @player, 0);
            """, new { tenant, session, player, code = session.ToString("N")[..8] }));
    }

    private Task SetCapabilityAsync(Guid tenant, string feature, bool enabled) =>
        ExecuteSystemAsync("""
            insert into tenant_capabilities (tenant_id, type, key, entitled, enabled)
            values (@tenant, 'feature', @feature, true, @enabled)
            on conflict (tenant_id, type, key) do update set enabled = excluded.enabled
            """, ("tenant", tenant), ("feature", feature), ("enabled", enabled));

    private Task<int> CountJobsAsync(Guid tenant) => ScalarSystemAsync<int>("select count(*)::int from jobs where tenant_id = @tenant", ("tenant", tenant));

    private Task<int> CountCleanupJobsAsync(Guid tenant) => ScalarSystemAsync<int>("select count(*)::int from jobs where tenant_id = @tenant and kind = 'cleanup-player'", ("tenant", tenant));

    private Task<long> SingleJobIdAsync(Guid tenant) => ScalarSystemAsync<long>("select id from jobs where tenant_id = @tenant and kind = 'event'", ("tenant", tenant));

    private Task<long> JobIdForSessionAsync(Guid tenant, Guid session) => ScalarSystemAsync<long>("""
        select id from jobs
        where tenant_id = @tenant and kind = 'event' and payload->>'subjectId' = @session
        """, ("tenant", tenant), ("session", session.ToString()));

    private Task<long> JobIdForFeatureAsync(Guid tenant, string feature) => ScalarSystemAsync<long>("""
        select id from jobs
        where tenant_id = @tenant and kind = 'event' and payload->>'feature' = @feature
        """, ("tenant", tenant), ("feature", feature));

    private Task<string> JobStatusAsync(long jobId) => ScalarSystemAsync<string>("select status from jobs where id = @jobId", ("jobId", jobId));

    private Task<string> JobStatusForFeatureAsync(Guid tenant, string feature) => ScalarSystemAsync<string>("""
        select status from jobs
        where tenant_id = @tenant and kind = 'event' and payload->>'feature' = @feature
        """, ("tenant", tenant), ("feature", feature));

    private Task<string> SessionResultAsync(Guid tenant, Guid session) => ScalarTenantAsync<string>(tenant,
        "select status || ':' || ended_at::text from sessions where id = @session", new { session });

    private Task<long> RevisionAsync(Guid tenant, Guid session) => ScalarTenantAsync<long>(tenant,
        "select revision from session_effects where session_id = @session", new { session });

    private Task<int> CountMarkersAsync(Guid tenant, string plugin, string key) =>
        ScalarTenantAsync<int>(tenant,
            "select count(*)::int from plugin_effects where plugin = @plugin and event_key = @key",
            new { plugin, key });

    private async Task<T> ScalarTenantAsync<T>(Guid tenant, string sql, object? parameters = null)
    {
        await using var sources = Sources();
        return (await new TenantDb(sources).InTenantAsync(tenant,
            transaction => transaction.QuerySingleOrDefaultAsync<T>(sql, parameters), readOnly: true))!;
    }

    private async Task<T> ScalarSystemAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteSystemAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RecordingNotifier : ISessionEffectsNotifier
    {
        private readonly object _gate = new();
        private readonly List<Guid> _sessions = [];

        public IReadOnlyList<Guid> Sessions
        {
            get
            {
                lock (_gate) return [.. _sessions];
            }
        }

        public void Changed(ITenantTx transaction, Guid sessionId)
        {
            lock (_gate) _sessions.Add(sessionId);
        }
    }

    private sealed class OffCapabilities : ICapabilities
    {
        public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default)
            where TSettings : notnull => throw new NotSupportedException();

        public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default)
            where TSettings : notnull => throw new NotSupportedException();
    }

    private sealed class RecordingLocks : ILocks
    {
        private readonly TenantLocks _inner = new();
        private int _playerCalls;

        public int PlayerCalls => Volatile.Read(ref _playerCalls);

        public Task PlayersAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _playerCalls);
            return _inner.PlayersAsync(transaction, ids, cancellationToken);
        }

        public Task ContentAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) =>
            _inner.ContentAsync(transaction, ids, cancellationToken);

        public Task OwnAsync(ITenantTx transaction, string table, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) =>
            _inner.OwnAsync(transaction, table, ids, cancellationToken);
    }

    private sealed class SampleFeature(string id, bool retired = false, int eventFailures = 0, int cleanupFailures = 0, bool cleanup = true, bool convergent = false) : IFeature
    {
        private int _eventFailures = eventFailures;
        private int _cleanupFailures = cleanupFailures;

        public string Id => id;
        public bool Retired => retired;
        public FeatureSettings Settings { get; } = FeatureSettings.Create(new SampleSettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
            feature.On<SessionEnded>(SessionEndedAsync);
            feature.On<PlayerUpgraded>(PlayerUpgradedAsync);
            if (cleanup) feature.CleanupPlayer(CleanupAsync);
        }

        private async Task SessionEndedAsync(ITenantTx transaction, SessionEnded @event, CancellationToken cancellationToken)
        {
            var key = convergent
                ? $"player:{await transaction.QuerySingleOrDefaultAsync<Guid>(\"select owner_id from sessions where id = @sessionId\", new { sessionId = @event.SessionId }, cancellationToken)}"
                : $"session:{@event.SessionId}";
            await WriteEffectAsync(transaction, key);
            if (Interlocked.Decrement(ref _eventFailures) >= 0) throw new InvalidOperationException("event failure");
        }

        private async Task PlayerUpgradedAsync(ITenantTx transaction, PlayerUpgraded @event, CancellationToken cancellationToken)
        {
            await WriteEffectAsync(transaction, $"player:{@event.PlayerId}");
            if (Interlocked.Decrement(ref _eventFailures) >= 0) throw new InvalidOperationException("event failure");
        }

        private async Task CleanupAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken)
        {
            await transaction.ExecuteAsync("""
                insert into plugin_effects (tenant_id, plugin, event_key)
                values (app_tenant(), @plugin, @key)
                on conflict do nothing
                """, new { plugin = $"cleanup:{Id}", key = $"player:{playerId}" }, cancellationToken);
            if (Interlocked.Decrement(ref _cleanupFailures) >= 0) throw new InvalidOperationException("cleanup failure");
        }

        private Task WriteEffectAsync(ITenantTx transaction, string key) =>
            transaction.ExecuteAsync("""
                insert into plugin_effects (tenant_id, plugin, event_key)
                values (app_tenant(), @plugin, @key)
                on conflict do nothing
                """, new { plugin = $"effect:{Id}", key });

        private sealed record SampleSettings;
    }
}
