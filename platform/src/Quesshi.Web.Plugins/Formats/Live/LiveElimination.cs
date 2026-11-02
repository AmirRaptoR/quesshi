using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>
/// Whether a participant has been eliminated, read from the seat status the session view already
/// carries. The server decides when a streak of misses eliminates a player, so a cold load or a
/// reconnect sees the same answer as a client that was connected all along.
/// </summary>
public static class LiveElimination
{
    public const string Eliminated = "eliminated";
    public const string Left = "left";

    public static bool IsEliminated(string seatStatus) => seatStatus == Eliminated;

    public static bool HasLeft(string seatStatus) => seatStatus == Left;

    /// <summary>The ids of the rostered seats that are eliminated, as far as the (possibly windowed) roster shows them.</summary>
    public static HashSet<string> EliminatedIds(SessionView view)
        => [.. Seats(view.Roster).Where(seat => IsEliminated(seat.Status)).Select(seat => seat.Id)];

    public static IEnumerable<SessionRosterSeat> Seats(RosterView roster) => roster switch
    {
        CompleteRosterView complete => complete.Seats,
        WindowedRosterView windowed => windowed.Top.Concat(windowed.Neighbours).DistinctBy(seat => seat.Id),
        _ => []
    };
}
