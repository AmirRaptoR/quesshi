using Quesshi.Shared;
using Quesshi.Web.Plugins.Formats.Live;

namespace Quesshi.Web.Tests.Live;

/// <summary>How the live result presents the roster it is handed: chips, abandoners and the order of the rows.</summary>
public class StandingsPresentationTests
{
    private static SessionRosterSeat Seat(string id, int seat, int? place, string outcome, string status = "done", int score = 0, string role = "player") =>
        new(id, id, seat, role, status, score, place, outcome);

    [Fact]
    public void A_shared_first_place_reads_as_a_draw_for_both_sharers_and_a_loss_below_them()
    {
        List<SessionRosterSeat> standings = [Seat("amir", 0, 1, "draw", score: 100), Seat("sara", 1, 1, "draw", score: 100), Seat("reza", 2, 3, "loss", score: 50)];

        Assert.All(standings.Take(2), row =>
        {
            Assert.Equal("chip--draw", StandingsPresentation.ChipClass(row.Outcome));
            Assert.Equal("result.outcome.draw", StandingsPresentation.OutcomeKey(row.Outcome));
        });
        Assert.Equal("chip--loss", StandingsPresentation.ChipClass(standings[2].Outcome));
        Assert.Equal("result.outcome.loss", StandingsPresentation.OutcomeKey(standings[2].Outcome));
        Assert.Equal([1, 1, 3], StandingsPresentation.Standings(new CompleteRosterView(standings)).Select(s => s.Place));
    }

    [Fact]
    public void An_outright_win_is_never_styled_as_a_draw()
    {
        Assert.Equal("chip--win", StandingsPresentation.ChipClass("win"));
        Assert.Equal("result.outcome.win", StandingsPresentation.OutcomeKey("win"));
    }

    [Fact]
    public void Standings_are_ordered_by_place_with_the_unplaced_last_and_spectators_left_out()
    {
        var roster = new CompleteRosterView([
            Seat("third", 0, 3, "loss"), Seat("watcher", 1, null, "none", role: "spectator"), Seat("none", 2, null, "none"),
            Seat("first", 3, 1, "win"), Seat("second", 4, 2, "loss")]);

        Assert.Equal(["first", "second", "third", "none"], StandingsPresentation.Standings(roster).Select(s => s.Id));
    }

    [Fact]
    public void A_windowed_roster_lists_each_seat_once()
    {
        var shared = Seat("sara", 1, 2, "loss");
        var roster = new WindowedRosterView(3, [Seat("amir", 0, 1, "win"), shared], [shared, Seat("reza", 2, 3, "loss")]);

        Assert.Equal(["amir", "sara", "reza"], StandingsPresentation.Standings(roster).Select(s => s.Id));
    }

    [Theory]
    [InlineData("left", true)]
    [InlineData("eliminated", true)]
    [InlineData("done", false)]
    [InlineData("active", false)]
    public void Only_a_seat_that_left_or_was_eliminated_is_an_abandoner(string status, bool abandoned)
    {
        Assert.Equal(abandoned, StandingsPresentation.Abandoned(Seat("amir", 0, 1, "loss", status)));
    }
}
