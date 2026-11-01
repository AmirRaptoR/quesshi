using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>
/// How a seat's outcome becomes a chip class and a translation key, and how a roster becomes an ordered
/// standings list — kept out of <c>StandingsList.razor</c>'s markup so the mappings are testable without bUnit.
/// </summary>
public static class StandingsPresentation
{
    public static string ChipClass(string? outcome) => outcome switch
    {
        "win" => "chip--win",
        "draw" => "chip--draw",
        _ => "chip--loss"
    };

    public static string OutcomeKey(string? outcome) => outcome switch
    {
        "win" => "result.outcome.win",
        "draw" => "result.outcome.draw",
        _ => "result.outcome.loss"
    };

    /// <summary>A player who left or was eliminated before the end abandoned the session.</summary>
    public static bool Abandoned(SessionRosterSeat seat) => seat.Status is "left" or "eliminated";

    /// <summary>The players of a roster by place, an unplaced seat after every placed one, then by seat; each player once.</summary>
    public static IReadOnlyList<SessionRosterSeat> Standings(RosterView roster)
    {
        IEnumerable<SessionRosterSeat> seats = roster switch
        {
            CompleteRosterView complete => complete.Seats,
            WindowedRosterView windowed => windowed.Top.Concat(windowed.Neighbours),
            _ => [],
        };
        return seats.Where(seat => seat.Role == "player").DistinctBy(seat => seat.Id)
            .OrderBy(seat => seat.Place ?? int.MaxValue).ThenBy(seat => seat.Seat).ToArray();
    }
}
