namespace Quesshi.Application.Ports;

/// <summary>Everything a target needs to render an invitation banner: who sent it and how long is left.</summary>
public sealed record LiveChallengeNotice(string ChallengeId, string ChallengerId, int Lang, int QuestionCount,
    List<string> CategoryIds, List<int> Levels, DateTimeOffset ExpiresAt);

/// <summary>
/// The outbound port <c>ILiveLobbyGrain</c> pushes through for both of its doors — the lobby's
/// counterpart to <see cref="ILiveNotifier"/>. The grain depends on this interface only, so it never
/// has to know whether the players it is queueing or challenging are actually listening on
/// <c>/hub/lobby</c>.
/// </summary>
public interface ILobbyNotifier
{
    /// <summary>Delivered to the target — on send, and again on connect if the challenge is still valid.</summary>
    Task ChallengeReceivedAsync(string targetId, LiveChallengeNotice challenge, CancellationToken ct = default);

    /// <summary>Delivered to the challenger only; the target's banner times out locally against <see cref="LiveChallengeNotice.ExpiresAt"/>.</summary>
    Task ChallengeExpiredAsync(string challengerId, string challengeId, CancellationToken ct = default);

    Task ChallengeDeclinedAsync(string challengerId, string challengeId, CancellationToken ct = default);

    /// <summary>The duel exists; navigate to <c>/live/{matchId}</c>. Delivered to both players.</summary>
    Task DuelReadyAsync(string playerId, string matchId, CancellationToken ct = default);

    /// <summary>Accepted, but the duel could not be built. Delivered to both players.</summary>
    Task ChallengeFailedAsync(string playerId, string challengeId, CancellationToken ct = default);

    /// <summary>Both players in a newly-formed random duel get this, with the same match id.</summary>
    Task MatchedAsync(string playerId, string matchId, CancellationToken ct = default);

    /// <summary>How many others are now waiting in this player's own (language, question count) bucket.</summary>
    Task QueueCountChangedAsync(string playerId, int othersWaiting, CancellationToken ct = default);

    /// <summary>The duel a match attempt would have created could not be built; this player is no longer queued.</summary>
    Task QueueFailedAsync(string playerId, CancellationToken ct = default);
}
