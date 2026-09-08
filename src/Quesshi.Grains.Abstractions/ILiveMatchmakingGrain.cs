namespace Quesshi.Grains.Abstractions;

/// <summary>
/// The single grain on key 0 that owns the random live queue. Renamed from <c>ILiveLobbyGrain</c>
/// (issue #51): that name collided with the per-duel lobby this project introduces — a duel now has
/// its own lobby phase, and this grain is really the <em>matchmaking</em> lobby, not a duel's.
/// </summary>
/// <remarks>
/// This grain used to also own friend challenges, deciding under one lock whether a player could hold
/// a commitment — a queue entry, a challenge sent, or a challenge received — across both doors at
/// once. That invariant is gone: a challenge is now a plain notification pointing at a lobby that
/// already exists (see <see cref="ChallengeAsync"/>), not a commitment competing with the queue for
/// the same slot, so nothing needs to arbitrate between the two any more. The queue keeps its single
/// lock for its own job, where the state being handed out — who is waiting, matched against whom — is
/// this grain's own, and the lock genuinely covers it.
/// </remarks>
[Alias("Quesshi.Grains.Abstractions.ILiveMatchmakingGrain")]
public interface ILiveMatchmakingGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Prunes, then looks for a waiting entry with the same language and question count belonging to
    /// somebody else. Found: removes it, builds the duel from that entry's categories and levels, and
    /// returns the new match id. Not found: replaces any existing entry for this player with a fresh
    /// one and returns null. If the duel cannot be built, neither player is left queued and null is
    /// returned to this caller too — <see cref="Quesshi.Application.Ports.ILobbyNotifier"/> is what
    /// tells both players it failed.
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
    /// From <paramref name="challengerId"/> to <paramref name="targetId"/>, pointing at the lobby
    /// <paramref name="lobbyId"/> already identifies — <c>ILiveMatchGrain</c>'s grain id, the same
    /// value <c>CreateLobbyAsync</c>/<c>CreateAsync</c> return as <c>LiveView.Id</c>. The challenge
    /// carries no settings of its own any more: the lobby already owns them, and accepting simply
    /// joins it (see <see cref="AcceptAsync"/>). Refused only for challenging yourself, or for a
    /// <paramref name="lobbyId"/> that either does not exist or is no longer in its lobby phase — every
    /// other outcome, including "the target already holds a dozen other pending invitations", is
    /// accepted: there is no exclusivity to enforce (see this interface's own remarks).
    /// <paramref name="challengeId"/> is minted by the caller, the same way a match id is minted
    /// before <c>CreateAsync</c>. Returns a <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>.
    /// </summary>
    [Alias("ChallengeAsync")]
    Task<int> ChallengeAsync(string challengeId, string challengerId, string targetId, string lobbyId);

    /// <summary>
    /// By the target: joins the lobby the challenge points at, via <c>ILiveMatchGrain.JoinAsync</c>.
    /// The challenge is consumed either way, whether or not the join actually succeeds — an
    /// invitation is a one-shot pointer, not a retryable commitment.
    /// </summary>
    [Alias("AcceptAsync")]
    Task<LiveChallengeAcceptResult> AcceptAsync(string challengeId, string targetId);

    /// <summary>By the target. Returns <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>.</summary>
    [Alias("DeclineAsync")]
    Task<int> DeclineAsync(string challengeId, string targetId);

    /// <summary>
    /// Everything to deliver to a player who has just connected: every still-valid invitation
    /// addressed to them, oldest first. Plural, not a single nullable view — with exclusivity gone, a
    /// player can hold several pending invitations at once, and a caller delivering on reconnect (see
    /// <c>LobbyHub.OnConnectedAsync</c>) must tell them about every one, not just one of them.
    /// </summary>
    [Alias("PendingForAsync")]
    Task<IReadOnlyList<LiveChallengeView>> PendingForAsync(string playerId);
}
