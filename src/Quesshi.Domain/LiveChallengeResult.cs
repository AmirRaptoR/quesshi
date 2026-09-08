namespace Quesshi.Domain;

/// <summary>
/// Why a challenge action did or did not go through. One enum for challenge, accept and decline —
/// and for the random queue's <c>EnqueueAsync</c> — because they all refuse for the same reason:
/// somebody involved already holds a commitment, or the thing being acted on is gone.
/// </summary>
public enum LiveChallengeResult
{
    /// <summary>Accepted for delivery (a challenge was sent, or a player was queued).</summary>
    Sent,
    Accepted,
    Declined,
    Expired,
    NotFound,
    NotYours,
    SelfChallenge,
    TargetOffline,

    /// <summary>The acting player already holds a commitment — a queue entry, a challenge sent, or one received.</summary>
    CallerCommitted,

    /// <summary>The other player already holds a commitment.</summary>
    TargetCommitted,

    /// <summary>The challenge was accepted, but the duel itself could not be built (no question set); dropped, both told.</summary>
    DuelFailed
}
