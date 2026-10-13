namespace Quesshi.Plugin.Api;

/// <summary>A completed session, represented only by its durable identifier.</summary>
public sealed record SessionEnded(Guid SessionId);

/// <summary>A player's upgrade, represented only by its durable identifier.</summary>
public sealed record PlayerUpgraded(Guid PlayerId);

/// <summary>Emits a feature event in the transaction that produced it.</summary>
public interface IEvents
{
    Task EmitAsync<TEvent>(ITenantTx transaction, TEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>Queues feature-owned player cleanup after the caller has marked a player deleted.</summary>
public interface IPlayerCleanup
{
    Task EnqueueAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the durable per-feature effects state for an ended session.</summary>
public interface ISessionEffects
{
    Task<SessionEffectsState> GetAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record SessionEffectsState(long Revision, IReadOnlyList<FeatureEffectState> Features);

public sealed record FeatureEffectState(string FeatureId, string Status);
