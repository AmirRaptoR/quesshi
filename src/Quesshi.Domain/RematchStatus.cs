namespace Quesshi.Domain;

/// <summary>
/// The outcome of a rematch request, carried across the grain boundary as an <c>int</c> the same way
/// <see cref="LiveJoinResult"/> is. There is no "waiting" state any more: a rematch no longer needs
/// every participant to press ready before anything happens (see <see cref="Created"/>'s own remarks),
/// so the first request either produces a lobby immediately or is refused outright.
/// </summary>
public enum RematchStatus
{
    /// <summary>Not a participant, the duel is not over, or it never got a second participant.</summary>
    Refused,

    /// <summary>
    /// A rematch lobby exists — either this call just created it, or an earlier request from anyone
    /// else already did and this call landed on that same one. Every participant of the finished duel
    /// is auto-invited; whoever turns up, plays. See <c>ILiveMatchGrain.RequestRematchAsync</c>'s own
    /// remarks for why the lobby's id is derived rather than minted, which is what makes this safe to
    /// call more than once.
    /// </summary>
    Created,

    /// <summary>The lobby could not be created — the live kill switch is off.</summary>
    Failed
}
