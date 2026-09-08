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

    /// <summary>
    /// Every seat is occupied — distinct from <see cref="Taken"/>, which now covers the other way a
    /// lobby can close before you get there: the owner pressing Start with room still unfilled. For a
    /// capacity-2 lobby the two can never actually differ (it can only ever close by filling), so this
    /// is the value a stranger sees there today where <see cref="Taken"/> used to be returned.
    /// </summary>
    Full,

    Unknown
}
