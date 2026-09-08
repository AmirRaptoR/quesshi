using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>One side of the duel bar: whoever it is, by the time this is built "mine" and every entry of "theirs" have already been decided.</summary>
public sealed record DuelBarSide(string PlayerId, string Name, string Avatar, int Score);

/// <summary>
/// Which participant is "you" and which are "them" depends only on who is asking —
/// <see cref="LiveViewDto"/> itself has no such notion, it only has an ordered list of seats. Kept as
/// a plain mapper so the duel-bar acceptance criterion is unit-tested without bUnit.
///
/// <see cref="Others"/> is issue #53's generalisation of what used to be a single <c>Theirs</c> side:
/// for a capacity-2 duel it is still exactly one entry, so <c>DuelBar.razor</c>'s existing markup for
/// that case renders unchanged; for a capacity&gt;2 duel it is every other seated player, in the same
/// join order <see cref="LiveViewDto.Participants"/> already keeps. An empty lobby seat is simply
/// absent from <see cref="LiveViewDto.Participants"/> rather than a phantom entry, so <see cref="Others"/>
/// is an empty list rather than a placeholder side — <c>DuelBar.razor</c> renders that as "nobody else
/// yet", not as an opponent scoring zero.
/// </summary>
public static class DuelBarScores
{
    public static (DuelBarSide Mine, List<DuelBarSide> Others) Split(LiveViewDto view, string meId)
    {
        var mine = SideFor(view, meId);
        var others = view.Participants.Where(p => p.PlayerId != meId).Select(p => SideFor(view, p.PlayerId)).ToList();

        return (mine, others);
    }

    private static DuelBarSide SideFor(LiveViewDto view, string playerId)
    {
        var info = view.Participants.FirstOrDefault(p => p.PlayerId == playerId);
        return new DuelBarSide(playerId, info?.Name ?? "", info?.Avatar ?? "", Score(view, playerId));
    }

    private static int Score(LiveViewDto view, string playerId)
        => view.Players.FirstOrDefault(p => p.PlayerId == playerId)?.Score ?? 0;
}
