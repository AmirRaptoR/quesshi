using Quesshi.Shared;
using Quesshi.Web.Plugins.Formats.Live;

namespace Quesshi.Web.Tests;

/// <summary>
/// The eliminated state is read from the seat status of the session view, so it is the same on a cold
/// load or a reconnect as for a client that watched the third miss happen.
/// </summary>
public class LiveEliminationTests
{
    private static SessionRosterSeat Seat(string id, string status) => new(id, id, 0, "player", status, 0, null, null);

    private static SessionView View(RosterView roster) => LobbyViews.Parse("amir", ["amir"], status: "running", format: "live") with { Roster = roster };

    [Fact]
    public void An_active_player_is_not_eliminated()
    {
        Assert.False(LiveElimination.IsEliminated("active"));
    }

    [Fact]
    public void A_player_whose_seat_is_eliminated_is_eliminated()
    {
        Assert.True(LiveElimination.IsEliminated("eliminated"));
        Assert.False(LiveElimination.HasLeft("eliminated"));
    }

    [Fact]
    public void A_player_who_left_is_not_eliminated_but_has_left()
    {
        Assert.False(LiveElimination.IsEliminated("left"));
        Assert.True(LiveElimination.HasLeft("left"));
    }

    [Fact]
    public void Only_the_rostered_seats_that_are_eliminated_are_named()
    {
        var view = View(new CompleteRosterView([Seat("amir", "active"), Seat("sara", "active"), Seat("reza", "eliminated")]));

        Assert.Equal(["reza"], LiveElimination.EliminatedIds(view));
    }

    [Fact]
    public void A_windowed_roster_names_eliminated_seats_from_the_top_and_the_neighbours_once()
    {
        var reza = Seat("reza", "eliminated");
        var view = View(new WindowedRosterView(100, [Seat("amir", "active"), reza], [reza, Seat("sara", "active")]));

        Assert.Equal(["reza"], LiveElimination.EliminatedIds(view));
    }
}
