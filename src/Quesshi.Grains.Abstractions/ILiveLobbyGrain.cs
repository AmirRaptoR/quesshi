namespace Quesshi.Grains.Abstractions;

/// <summary>
/// The single grain on key 0 that decides, under one lock, whether a player may enter a duel through
/// either door: the random queue, or a friend challenge. A player holds at most one commitment across
/// both — a queue entry, a challenge sent, or a challenge received — which is why both doors live on
/// the same grain rather than two.
/// </summary>
[Alias("Quesshi.Grains.Abstractions.ILiveLobbyGrain")]
public interface ILiveLobbyGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Minimal placeholder for #26's random queue: enough of "waiting" to prove the one-commitment
    /// invariant against a friend challenge. Pairing two waiting players is #26's own work.
    /// Returns a <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>: <c>Sent</c> or <c>CallerCommitted</c>.
    /// </summary>
    [Alias("EnqueueAsync")]
    Task<int> EnqueueAsync(string playerId, int lang);

    [Alias("LeaveQueueAsync")]
    Task LeaveQueueAsync(string playerId);

    /// <summary>
    /// From <paramref name="challengerId"/> to <paramref name="targetId"/>, carrying the language,
    /// question count, categories and levels the challenger picked. <paramref name="challengeId"/> is
    /// minted by the caller (the hub), the same way a match id is minted before <c>CreateAsync</c>.
    /// Returns a <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>.
    /// </summary>
    [Alias("ChallengeAsync")]
    Task<int> ChallengeAsync(string challengeId, string challengerId, string targetId, int lang,
        int questionCount, List<string> categoryIds, List<int> levels);

    /// <summary>
    /// By the target. On success this also builds the duel — <c>ILiveMatchGrain.CreateAsync</c> for
    /// the challenger then <c>JoinAsync</c> for the target — and frees both players' commitments.
    /// </summary>
    [Alias("AcceptAsync")]
    Task<LiveChallengeAcceptResult> AcceptAsync(string challengeId, string targetId);

    /// <summary>By the target. Frees both players' commitments and returns <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>.</summary>
    [Alias("DeclineAsync")]
    Task<int> DeclineAsync(string challengeId, string targetId);

    /// <summary>What to deliver to a player who has just connected: their pending challenge as a target, if any and still valid.</summary>
    [Alias("PendingForAsync")]
    Task<LiveChallengeView?> PendingForAsync(string playerId);
}
