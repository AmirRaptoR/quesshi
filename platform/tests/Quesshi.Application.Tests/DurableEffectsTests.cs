using System.Data;
using System.Text.Json;
using Quesshi.Application.Jobs;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Tests;

public sealed class DurableEffectsTests
{
    [Fact]
    public async Task Emit_queues_only_enabled_active_feature_handlers_with_identifier_payloads()
    {
        var registry = CreateRegistry(new EventFeature("active"), new RetiredEventFeature("retired"));
        var jobs = new RecordingJobs();
        var events = new DurableEvents(registry, jobs, new UnusedLocks(), new NullSessionEffectsNotifier());
        var sessionId = Guid.NewGuid();

        await events.EmitAsync(new CapabilityTransaction("active"), new SessionEnded(sessionId));

        var queued = Assert.Single(jobs.Queued);
        Assert.Equal("core", queued.Owner);
        Assert.Equal("event", queued.Kind);
        Assert.Equal($"active:sessionEnded:{sessionId:N}", queued.UniqueKey);
        Assert.Equal("active", queued.Payload.GetProperty("feature").GetString());
        Assert.Equal("sessionEnded", queued.Payload.GetProperty("eventType").GetString());
        Assert.Equal(sessionId, queued.Payload.GetProperty("subjectId").GetGuid());
        Assert.False(queued.Payload.TryGetProperty("session", out _));
    }

    [Fact]
    public async Task Emit_does_not_backfill_a_feature_that_was_off_when_the_event_was_emitted()
    {
        var registry = CreateRegistry(new EventFeature("active"));
        var jobs = new RecordingJobs();
        var events = new DurableEvents(registry, jobs, new UnusedLocks(), new NullSessionEffectsNotifier());

        await events.EmitAsync(new CapabilityTransaction(), new SessionEnded(Guid.NewGuid()));

        Assert.Empty(jobs.Queued);
    }

    [Fact]
    public async Task Session_handler_starts_with_marker_and_advances_revision_before_notifying()
    {
        var registry = CreateRegistry(new EventFeature("active"));
        var transaction = new EffectTransaction();
        var notifier = new RecordingNotifier();
        var events = new DurableEvents(registry, new RecordingJobs(), new UnusedLocks(), notifier);
        var sessionId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(EventJobPayload.Create("active", "sessionEnded", sessionId));

        await events.HandleEventJobAsync(new JobContext(1, Guid.NewGuid(), "core", "event", payload, new BoundTenantDb(transaction)), CancellationToken.None);

        Assert.StartsWith("insert into plugin_effects", transaction.Commands[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(transaction.Commands, command => command.StartsWith("insert into session_effects", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(sessionId, Assert.Single(notifier.Sessions));
    }

    [Fact]
    public async Task Effects_reader_reports_off_for_declared_handler_without_a_persisted_job()
    {
        var registry = CreateRegistry(new EventFeature("active"));
        var events = new DurableEvents(registry, new RecordingJobs(), new UnusedLocks(), new NullSessionEffectsNotifier());

        var state = await events.GetAsync(new EffectTransaction(), Guid.NewGuid());

        var feature = Assert.Single(state.Features);
        Assert.Equal("active", feature.FeatureId);
        Assert.Equal("off", feature.Status);
    }

    [Fact]
    public async Task Cleanup_queues_unlimited_work_only_after_the_player_is_marked_deleted()
    {
        var registry = CreateRegistry(new CleanupFeature("cleanup"));
        var jobs = new RecordingJobs();
        var events = new DurableEvents(registry, jobs, new UnusedLocks(), new NullSessionEffectsNotifier());
        var playerId = Guid.NewGuid();

        await events.EnqueueAsync(new PlayerDeletionTransaction(deleted: false), playerId);
        await events.EnqueueAsync(new PlayerDeletionTransaction(deleted: true), playerId);

        var queued = Assert.Single(jobs.Queued);
        Assert.Equal("cleanup-player", queued.Kind);
        Assert.Equal($"cleanup:{playerId:N}", queued.UniqueKey);
        Assert.Null(queued.MaxAttempts);
        Assert.Equal(playerId, queued.Payload.GetProperty("playerId").GetGuid());
    }

    private static FeatureRegistry CreateRegistry(params IFeature[] features)
    {
        var handlers = new JobHandlerRegistry();
        var registry = new FeatureRegistry(new CapabilityRegistry(), handlers, new JobScheduleRegistry(handlers));
        foreach (var feature in features) registry.Register(feature);
        return registry;
    }

    private class EventFeature(string id) : IFeature
    {
        public string Id => id;
        public FeatureSettings Settings { get; } = FeatureSettings.Create(new object(), _ => []);

        public void Configure(IFeatureBuilder feature) =>
            feature.On<SessionEnded>((_, _, _) => Task.CompletedTask);
    }

    private sealed class RetiredEventFeature(string id) : EventFeature(id)
    {
        public bool Retired => true;
    }

    private sealed class CleanupFeature(string id) : IFeature
    {
        public string Id => id;
        public FeatureSettings Settings { get; } = FeatureSettings.Create(new object(), _ => []);

        public void Configure(IFeatureBuilder feature) =>
            feature.CleanupPlayer((_, _, _) => Task.CompletedTask);
    }

    private sealed class CapabilityTransaction(params string[] enabled) : ITenantTx
    {
        private readonly HashSet<string> enabled = enabled.ToHashSet(StringComparer.Ordinal);

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>([]);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (typeof(T) == typeof(bool?))
            {
                var featureId = parameters!.GetType().GetProperty("featureId")!.GetValue(parameters) as string;
                return Task.FromResult((T?)(object?)(bool?)enabled.Contains(featureId!));
            }

            return Task.FromResult(default(T));
        }
    }

    private sealed class RecordingJobs : IJobs
    {
        public List<QueuedJob> Queued { get; } = [];

        public Task EnqueueAsync(ITenantTx transaction, string owner, string kind, JsonElement payload, string? uniqueKey = null, DateTimeOffset? runAt = null, int? maxAttempts = 10, CancellationToken cancellationToken = default)
        {
            Queued.Add(new QueuedJob(owner, kind, payload, uniqueKey, maxAttempts));
            return Task.CompletedTask;
        }
    }

    private sealed record QueuedJob(string Owner, string Kind, JsonElement Payload, string? UniqueKey, int? MaxAttempts);

    private sealed class EffectTransaction : ITenantTx
    {
        public List<string> Commands { get; } = [];

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>([]);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(sql.TrimStart());
            return Task.FromResult(1);
        }

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(sql.TrimStart());
            if (sql.StartsWith("insert into plugin_effects", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult((T?)(object?)1);
            return Task.FromResult(default(T));
        }
    }

    private sealed class PlayerDeletionTransaction(bool deleted) : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>([]);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((T?)(object?)(bool?)deleted);
    }

    private sealed class BoundTenantDb(ITenantTx transaction) : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) =>
            callback(transaction);
    }

    private sealed class RecordingNotifier : ISessionEffectsNotifier
    {
        public List<Guid> Sessions { get; } = [];

        public void Changed(ITenantTx transaction, Guid sessionId) => Sessions.Add(sessionId);
    }

    private sealed class UnusedLocks : ILocks
    {
        public Task PlayersAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ContentAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OwnAsync(ITenantTx transaction, string table, IEnumerable<Guid> keys, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
