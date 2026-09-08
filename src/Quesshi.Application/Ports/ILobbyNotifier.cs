namespace Quesshi.Application.Ports;

/// <summary>
/// Everything a target needs to render an invitation banner: who sent it, which lobby it points at,
/// and how long is left. No settings ride along any more — the challenge is a pointer at a lobby that
/// already owns them, not a duplicate of them (see <c>ILiveMatchmakingGrain.ChallengeAsync</c>'s own
/// remarks). <see cref="LobbyCode"/> is the human-shareable one; <see cref="LobbyId"/> is what
/// actually resolves the lobby's grain, which is what accepting joins.
/// </summary>
public sealed record LiveChallengeNotice(string ChallengeId, string ChallengerId, string LobbyId, string LobbyCode, DateTimeOffset ExpiresAt);

/// <summary>
/// The outbound port <c>ILiveMatchmakingGrain</c> pushes through for both of its doors — the lobby's
/// counterpart to <see cref="ILiveNotifier"/>. The grain depends on this interface only, so it never
/// has to know whether the players it is queueing or challenging are actually listening on
/// <c>/hub/lobby</c>.
/// </summary>
public interface ILobbyNotifier
{
    /// <summary>
    /// Delivered to the target — on send, and again on every connect for as long as the invitation is
    /// still valid (its lobby's own lifetime, not a fixed 45 seconds: see
    /// <c>ILiveMatchmakingGrain.ChallengeAsync</c>). Called once per pending invitation: with
    /// exclusivity gone, a target can hold several at once, so a caller delivering on reconnect must
    /// call this for each one <c>PendingForAsync</c> returns, not just the first.
    /// </summary>
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
