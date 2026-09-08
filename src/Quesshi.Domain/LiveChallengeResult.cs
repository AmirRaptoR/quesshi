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
    /// The challenge was accepted, but the lobby it pointed at could not be joined — it had already
    /// filled, started, or expired by the time this landed. Dropped either way: an invitation is
    /// consumed the instant it is acted on, whether or not the seat was still there.
    /// </summary>
    DuelFailed
}
