using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.AsyncDuel;

/// <summary>One side of the duel bar: whoever it is, by the time this is built "mine" and every entry of "theirs" have already been decided.</summary>
public sealed record DuelBarSide(string PlayerId, string Name, int Score);

/// <summary>
/// Which seat is "you" and which are "them" depends only on who is asking — a roster only has an ordered list
/// of seats. Kept as a plain mapper so the duel bar is unit-tested without bUnit. For a capacity-2 duel
/// <see cref="Others"/> is exactly one entry; an empty seat is absent from the roster, so it yields no phantom side.
/// </summary>
public static class DuelBarScores
{
    public static (DuelBarSide? Mine, List<DuelBarSide> Others) Split(IReadOnlyList<SessionRosterSeat> players, string meId)
    {
        var mine = players.FirstOrDefault(seat => seat.Id == meId);
        return (
            mine is null ? null : Side(mine),
            players.Where(seat => seat.Id != meId).Select(Side).ToList());
    }

    private static DuelBarSide Side(SessionRosterSeat seat) => new(seat.Id, seat.Name, seat.Score);
}
