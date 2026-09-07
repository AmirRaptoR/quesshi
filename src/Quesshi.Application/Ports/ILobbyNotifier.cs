namespace Quesshi.Application.Ports;

/// <summary>
/// The outbound port <c>ILiveLobbyGrain</c> pushes through — the lobby's counterpart to
/// <see cref="ILiveNotifier"/>. The grain depends on this interface only, so it never has to know
/// whether the players it is queueing are actually listening on <c>/hub/lobby</c>.
/// </summary>
public interface ILobbyNotifier
{
    /// <summary>Both players in a newly-formed duel get this, with the same match id.</summary>
    Task MatchedAsync(string playerId, string matchId, CancellationToken ct = default);

    /// <summary>How many others are now waiting in this player's own (language, question count) bucket.</summary>
    Task QueueCountChangedAsync(string playerId, int othersWaiting, CancellationToken ct = default);

    /// <summary>The duel a match attempt would have created could not be built; this player is no longer queued.</summary>
    Task QueueFailedAsync(string playerId, CancellationToken ct = default);
}
