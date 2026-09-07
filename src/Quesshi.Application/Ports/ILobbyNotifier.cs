namespace Quesshi.Application.Ports;

/// <summary>Everything a target needs to render an invitation banner: who sent it and how long is left.</summary>
public sealed record LiveChallengeNotice(string ChallengeId, string ChallengerId, int Lang, int QuestionCount,
    List<string> CategoryIds, List<int> Levels, DateTimeOffset ExpiresAt);

/// <summary>
/// The outbound port the live lobby grain pushes challenge events through, over <c>/hub/lobby</c>.
/// Lives beside <see cref="ILiveNotifier"/> for the same reason: the grain never knows whether the
/// target is actually listening, or on which page.
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
}
