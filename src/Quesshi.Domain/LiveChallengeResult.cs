namespace Quesshi.Domain;

/// <summary>
/// Why a challenge action did or did not go through. One enum for challenge, accept and decline.
/// Unlike before lobbies existed, sending or receiving a challenge is never refused for already
/// holding another one — see <c>ILiveMatchmakingGrain</c>'s own remarks on why that exclusivity is
/// gone: an invitation is now a plain notification pointing at a lobby, and being invited to (or
/// inviting into) several at once is normal.
/// </summary>
public enum LiveChallengeResult
{
    /// <summary>Accepted for delivery — the challenge now exists and the target has been told.</summary>
    Sent,

    /// <summary>The target joined the lobby the challenge pointed at.</summary>
    Accepted,
    Declined,
    Expired,
    NotFound,
    NotYours,
    SelfChallenge,

    /// <summary>
    /// The challenge was accepted, but the lobby it pointed at could not be joined for a reason not
    /// worth a value of its own — the target already owned the lobby, or it had vanished outright
    /// (<c>LiveJoinResult.SelfJoin</c>/<c>Unknown</c>, neither reachable through a legitimate
    /// invitation). Dropped either way: an invitation is consumed the instant it is acted on, whether
    /// or not the seat was still there. See <see cref="LobbyFull"/>/<see cref="LobbyTaken"/> for the
    /// two failure shapes worth telling apart from this one and from each other.
    /// </summary>
    DuelFailed,

    /// <summary>
    /// Accepted too late: every seat was already occupied by the time this landed
    /// (<c>LiveJoinResult.Full</c>). Issue #53's own ask — surfaced separately from
    /// <see cref="LobbyTaken"/> and <see cref="DuelFailed"/> rather than folded into one generic
    /// failure, so the player who just missed a seat is told that, not left to guess.
    /// </summary>
    LobbyFull,

    /// <summary>
    /// Accepted too late a different way: the owner started the duel with the lobby still short of
    /// full (<c>LiveJoinResult.Taken</c>) — a capacity-2 lobby can never actually produce this (it
    /// only ever closes by filling), so it is reachable only for a wider N-player lobby.
    /// </summary>
    LobbyTaken
}
