using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Whether a participant has been eliminated — dropped for missing <c>LiveRules.MissesBeforeAbandon</c>
/// rounds in a row — derived from the running <c>MissStreak</c> the wire already carries per player.
/// <c>LiveMatch.CloseRound</c> stops touching an abandoned player's streak the instant they drop out
/// (they leave <c>ActiveParticipants</c> for good), so it sits frozen at the threshold forever after —
/// which is what makes this reconstructible from a fresh <see cref="LiveViewDto"/> alone, on a cold
/// load or a reconnect's rejoin, rather than depending on having been connected for the live
/// <c>PlayerEliminated</c> push that announced it in the moment.
///
/// <see cref="LiveViewDto.Players"/>'s <c>MissStreak</c> is only ever refreshed by a full view
/// (Join/Rejoin/GET) — an ordinary round reveal updates score and correctness but not this field — so
/// this helper is the seed for a page's own running elimination set, not something recomputed every
/// round from <c>_view</c> alone; see <c>Live.razor</c>'s own remarks for why it also listens to the
/// live push directly.
///
/// Mirrors <c>LiveRules.MissesBeforeAbandon</c> (Domain, 3) rather than referencing it: the Web
/// project has no reference to Domain, the same reasoning <c>Live.razor</c>'s own <c>RevealSeconds</c>
/// mirror gives for <c>LiveRules.RevealTime</c>.
/// </summary>
public static class LiveElimination
{
    public const int MissesBeforeAbandon = 3;

    public static bool IsEliminated(LivePlayerViewDto player) => player.MissStreak >= MissesBeforeAbandon;

    public static HashSet<string> EliminatedIds(LiveViewDto view)
        => [.. view.Players.Where(IsEliminated).Select(p => p.PlayerId)];
}
