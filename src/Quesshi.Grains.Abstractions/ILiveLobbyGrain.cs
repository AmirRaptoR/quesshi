namespace Quesshi.Grains.Abstractions;

/// <summary>
/// The single grain on key 0 that decides, under one lock, whether a player may enter a duel through
/// either door: the random queue, or a friend challenge. A player holds at most one commitment across
/// both — a queue entry, a challenge sent, or a challenge received — which is why both doors live on
/// the same grain rather than two: Orleans runs this grain's calls one after another, never
/// concurrently, and that is the entire mechanism behind the invariant.
/// </summary>
[Alias("Quesshi.Grains.Abstractions.ILiveLobbyGrain")]
public interface ILiveLobbyGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Prunes, then looks for a waiting entry with the same language and question count belonging to
    /// somebody else. Found: removes it, builds the duel from that entry's categories and levels, and
    /// returns the new match id. Not found: replaces any existing entry for this player with a fresh
    /// one and returns null. If the duel cannot be built, neither player is left queued and null is
    /// returned to this caller too — <see cref="Quesshi.Application.Ports.ILobbyNotifier"/> is what
    /// tells both players it failed. A caller already holding a pending challenge is refused outright:
    /// nothing is queued and null comes back.
    /// </summary>
    [Alias("EnqueueAsync")]
    Task<string?> EnqueueAsync(string playerId, int lang, int questionCount, List<string> categories, List<int> levels);

    [Alias("LeaveAsync")]
    Task LeaveAsync(string playerId);

    [Alias("HeartbeatAsync")]
    Task HeartbeatAsync(string playerId);

    /// <summary>How many others (never the caller) are queued in the caller's own bucket (same
    /// language, same question count), after pruning.</summary>
    [Alias("WaitingCountAsync")]
    Task<int> WaitingCountAsync(string playerId, int lang, int questionCount);

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
