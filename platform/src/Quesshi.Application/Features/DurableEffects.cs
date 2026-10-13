using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Plugin.Api;

namespace Quesshi.Application;

/// <summary>
/// A transaction-bound seam for the later effects stream. Implementations must
/// arrange delivery only after the transaction commits.
/// </summary>
internal interface ISessionEffectsNotifier
{
    void Changed(ITenantTx transaction, Guid sessionId);
}

internal sealed class NullSessionEffectsNotifier : ISessionEffectsNotifier
{
    public void Changed(ITenantTx transaction, Guid sessionId)
    {
    }
}

internal sealed class DurableEvents(
    FeatureRegistry features,
    IJobs jobs,
    ILocks locks,
    ISessionEffectsNotifier notifications) : IEvents, IPlayerCleanup, ISessionEffects
{
    private const string Owner = "core";
    private const string EventKind = "event";
    private const string CleanupKind = "cleanup-player";

    public async Task EmitAsync<TEvent>(ITenantTx transaction, TEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(@event);
        var (eventType, subjectId) = EventIdentity.From(@event);

        foreach (var featureId in features.EventHandlers
                     .Where(pair => pair.Key.EventType == typeof(TEvent))
                     .Select(pair => pair.Key.FeatureId)
                     .Order(StringComparer.Ordinal))
        {
            var enabled = await transaction.QuerySingleOrDefaultAsync<bool?>(
                "select entitled and enabled from tenant_capabilities where type = 'feature' and key = @featureId",
                new { featureId },
                cancellationToken);
            if (enabled != true) continue;

            var payload = EventJobPayload.Create(featureId, eventType, subjectId);
            await jobs.EnqueueAsync(
                transaction,
                Owner,
                EventKind,
                JsonSerializer.SerializeToElement(payload),
                $"{featureId}:{eventType}:{subjectId:N}",
                cancellationToken: cancellationToken);
        }
    }

    public async Task EnqueueAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var deleted = await IsPlayerDeletedAsync(transaction, playerId, cancellationToken);
        if (deleted != true) return;

        foreach (var featureId in features.CleanupHandlers.Keys.Order(StringComparer.Ordinal))
        {
            var payload = CleanupJobPayload.Create(featureId, playerId);
            await jobs.EnqueueAsync(
                transaction,
                Owner,
                CleanupKind,
                JsonSerializer.SerializeToElement(payload),
                $"{featureId}:{playerId:N}",
                maxAttempts: null,
                cancellationToken: cancellationToken);
        }
    }

    public async Task<SessionEffectsState> GetAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var revision = await transaction.QuerySingleOrDefaultAsync<long?>(
            "select revision from session_effects where session_id = @sessionId",
            new { sessionId },
            cancellationToken) ?? 0;
        var jobsByFeature = (await transaction.QueryAsync<PersistedEffectJob>(
            "select plugin, status from session_ended_effect_jobs(@sessionId)",
            new { sessionId },
            cancellationToken)).ToDictionary(job => job.Plugin, job => job.Status, StringComparer.Ordinal);
        var applied = (await transaction.QueryAsync<string>(
            "select plugin from plugin_effects where event_key = @eventKey",
            new { eventKey = $"sessionEnded:{sessionId}" },
            cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var featureIds = features.DeclaredEventHandlers.Keys
            .Where(key => key.EventType == typeof(SessionEnded))
            .Select(key => key.FeatureId)
            .Concat(jobsByFeature.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var states = featureIds.Select(featureId => new FeatureEffectState(
            featureId,
            applied.Contains(featureId) ? "done" :
            jobsByFeature.TryGetValue(featureId, out var status) ? ToEffectStatus(status) :
            "off")).ToArray();
        return new SessionEffectsState(revision, states);
    }

    internal async Task HandleEventJobAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.Payload.Deserialize<EventJobPayload>()
            ?? throw new InvalidOperationException("Event job payload is invalid.");
        var database = context.Database ?? throw new InvalidOperationException("Event jobs require a tenant.");
        var eventValue = EventIdentity.Create(payload.EventType, payload.SubjectId);

        await database.InTenantAsync(context.TenantId!.Value, async transaction =>
        {
            // This is deliberately the first command in the handler transaction.
            var markerInserted = await transaction.QuerySingleOrDefaultAsync<int?>(
                "insert into plugin_effects (tenant_id, plugin, event_key) values (app_tenant(), @plugin, @eventKey) on conflict do nothing returning 1",
                new { plugin = payload.FeatureId, eventKey = payload.EventKey },
                cancellationToken);
            if (markerInserted is null) return 0;

            if (eventValue is PlayerUpgraded playerUpgraded)
            {
                await locks.PlayersAsync(transaction, [playerUpgraded.PlayerId], cancellationToken);
                if (await IsPlayerDeletedAsync(transaction, playerUpgraded.PlayerId, cancellationToken) == true) return 0;
            }
            else if (eventValue is SessionEnded endedSession)
            {
                var playerIds = await transaction.QueryAsync<Guid>(
                    "select player_id from session_participants where session_id = @sessionId order by player_id",
                    new { sessionId = endedSession.SessionId },
                    cancellationToken);
                await locks.PlayersAsync(transaction, playerIds, cancellationToken);
            }

            await features.InvokeEventHandlerAsync(payload.FeatureId, eventValue.GetType(), transaction, eventValue, cancellationToken);
            if (eventValue is SessionEnded sessionEnded)
            {
                await IncrementSessionRevisionAsync(transaction, sessionEnded.SessionId, cancellationToken);
                notifications.Changed(transaction, sessionEnded.SessionId);
            }

            return 0;
        }, cancellationToken: cancellationToken);
    }

    internal async Task HandleCleanupJobAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.Payload.Deserialize<CleanupJobPayload>()
            ?? throw new InvalidOperationException("Player cleanup job payload is invalid.");
        var database = context.Database ?? throw new InvalidOperationException("Cleanup jobs require a tenant.");

        await database.InTenantAsync(context.TenantId!.Value, async transaction =>
        {
            await locks.PlayersAsync(transaction, [payload.PlayerId], cancellationToken);
            if (await IsPlayerDeletedAsync(transaction, payload.PlayerId, cancellationToken) == true)
            {
                await features.InvokeCleanupAsync(payload.FeatureId, transaction, payload.PlayerId, cancellationToken);
            }

            return 0;
        }, cancellationToken: cancellationToken);
    }

    private static Task<bool?> IsPlayerDeletedAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken) =>
        transaction.QuerySingleOrDefaultAsync<bool?>(
            "select deleted_at is not null from players where id = @playerId",
            new { playerId },
            cancellationToken);

    private static Task<int> IncrementSessionRevisionAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            "insert into session_effects (tenant_id, session_id, revision) values (app_tenant(), @sessionId, 1) on conflict (tenant_id, session_id) do update set revision = session_effects.revision + 1",
            new { sessionId },
            cancellationToken);

    private static string ToEffectStatus(string status) => status switch
    {
        "dead" => "failed",
        "ready" => "pending",
        "done" => "done",
        _ => "failed"
    };

    private sealed record PersistedEffectJob(string Plugin, string Status);
}

internal sealed record EventJobPayload(
    [property: JsonPropertyName("feature")] string FeatureId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("subjectId")] Guid SubjectId)
{
    public string EventKey => $"{EventType}:{SubjectId}";

    public static EventJobPayload Create(string featureId, string eventType, Guid subjectId) => new(featureId, eventType, subjectId);
}

internal sealed record CleanupJobPayload(
    [property: JsonPropertyName("feature")] string FeatureId,
    [property: JsonPropertyName("playerId")] Guid PlayerId)
{
    public static CleanupJobPayload Create(string featureId, Guid playerId) => new(featureId, playerId);
}

internal static class EventIdentity
{
    public static (string EventType, Guid SubjectId) From<TEvent>(TEvent @event) => @event switch
    {
        SessionEnded sessionEnded => ("sessionEnded", sessionEnded.SessionId),
        PlayerUpgraded playerUpgraded => ("playerUpgraded", playerUpgraded.PlayerId),
        _ => throw new ArgumentOutOfRangeException(nameof(@event), "Only registered durable feature events can be emitted.")
    };

    public static object Create(string eventType, Guid subjectId) => eventType switch
    {
        "sessionEnded" => new SessionEnded(subjectId),
        "playerUpgraded" => new PlayerUpgraded(subjectId),
        _ => throw new InvalidOperationException($"Unknown durable event type '{eventType}'.")
    };
}
