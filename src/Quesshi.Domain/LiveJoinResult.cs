namespace Quesshi.Domain;

/// <summary>
/// Why a join did or did not seat a player, distinguished because <c>GetAsync</c> returns null for
/// someone who is not yet a participant — this is the grain's only other way to tell "expired" from
/// "already taken" apart, and both from an idempotent re-join by the same opponent.
/// </summary>
public enum LiveJoinResult
{
    Joined,
    AlreadyIn,
    SelfJoin,
    Taken,
    Expired,
    Unknown
}
