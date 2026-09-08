using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The results screen's standings list, replacing the old two-score view (issue #53). Ranking itself
/// is computed server-side (<c>Standing</c>/<c>GameEndpoints.BuildStandingsAsync</c>); this covers how
/// the Web layer presents whatever <see cref="StandingRowDto"/> rows it is handed, extracted out of
/// <c>StandingsList.razor</c>'s markup the same way every other render-mapping in this project is —
/// see <see cref="StandingsPresentation"/>'s own remarks.
/// </summary>
public class StandingsPresentationTests
{
    /// <summary>
    /// The scenario the spec calls out by name: scores of 100/100/50 must read as two draws and one
    /// loss, never three draws and never a plain "win" for either of the sharers — <c>WinnerId</c> is
    /// null and <c>IsDraw</c> is true for the match as a whole, but each player's own row is what
    /// decides their chip, and a shared first place is a draw for both of them.
    /// </summary>
    [Fact]
    public void A_shared_first_place_reads_as_a_draw_for_both_sharers_and_a_loss_below_them()
    {
        List<StandingRowDto> standings =
        [
            new("amir", "Amir", "a", 100, 1, "draw"),
            new("sara", "Sara", "s", 100, 1, "draw"),
            new("reza", "Reza", "r", 50, 3, "loss"),
        ];

        Assert.All(standings.Take(2), row =>
        {
            Assert.Equal("chip--draw", StandingsPresentation.ChipClass(row.Outcome));
            Assert.Equal("result.outcome.draw", StandingsPresentation.OutcomeKey(row.Outcome));
        });

        Assert.Equal("chip--loss", StandingsPresentation.ChipClass(standings[2].Outcome));
        Assert.Equal("result.outcome.loss", StandingsPresentation.OutcomeKey(standings[2].Outcome));

        // Competition ranking: the tied pair share place 1, and the row below them skips ahead by
        // how many shared it — 1, 1, 3, never 1, 1, 2.
        Assert.Equal([1, 1, 3], standings.Select(s => s.Place));
    }

    [Fact]
    public void An_outright_win_is_never_styled_as_a_draw()
    {
        var winner = new StandingRowDto("amir", "Amir", "a", 100, 1, "win");

        Assert.Equal("chip--win", StandingsPresentation.ChipClass(winner.Outcome));
        Assert.Equal("result.outcome.win", StandingsPresentation.OutcomeKey(winner.Outcome));
    }

    /// <summary>
    /// An async run forfeiture finalised with an unfinished seat: the row still carries a real,
    /// ranked outcome from whatever that player banked (see <see cref="StandingRowDto"/>'s own
    /// remarks), and <see cref="StandingRowDto.Expired"/> is shown alongside that outcome, not
    /// instead of it — expiry is not itself an outcome, so it must never mask the chip that says
    /// whether this row actually won, drew or lost.
    /// </summary>
    [Fact]
    public void An_expired_run_still_carries_its_own_ranked_outcome_alongside_the_expired_flag()
    {
        var expiredLoser = new StandingRowDto("sara", "Sara", "s", 20, 2, "loss", Expired: true);

        Assert.True(expiredLoser.Expired);
        Assert.Equal("chip--loss", StandingsPresentation.ChipClass(expiredLoser.Outcome));
    }

    [Fact]
    public void A_standing_row_is_not_expired_by_default()
    {
        var finished = new StandingRowDto("amir", "Amir", "a", 100, 1, "win");

        Assert.False(finished.Expired);
    }
}
