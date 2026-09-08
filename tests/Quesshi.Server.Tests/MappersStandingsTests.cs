using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>
/// Every per-player outcome has to read <c>Standing</c>/<c>ParticipantResult</c>, never the two
/// match-wide scalars <c>WinnerId</c>/<c>IsDraw</c> — those name only who occupies (or shares) first
/// place, and <c>Mappers.ToSummary</c>/<c>ToLiveSummary</c> used to derive every other player's own
/// result from them anyway. For three players scored 100, 100, 50, <c>LiveMatch.BuildStandings</c>
/// puts both 100s in a shared first place — <c>Draw</c> for each of them, not <c>Win</c> for either —
/// and the 50 in third at a <c>Loss</c>. Reading the old scalars (<c>IsDraw = true</c> because first
/// place is shared, <c>WinnerId = null</c>) would have hung a "draw" on all three; this proves it
/// hangs one on exactly the two who share it.
/// </summary>
public class MappersStandingsTests
{
    private static (string Name, string Avatar) Lookup(string id) => (id, id);

    [Fact]
    public void ToLiveSummary_100_100_50_reads_two_draws_and_a_loss_from_Results_not_the_scalars()
    {
        // The row a genuinely-settled three-player duel would archive: first place shared between the
        // two 100s (each Draw, per Standing's own "several sharers each draw" rule), the 50 alone in
        // third (Loss) — WinnerId null and IsDraw true because the *match* has no sole winner, exactly
        // as they would be for an ordinary two-player draw, which is precisely why a per-player read of
        // them alone cannot tell the 50 apart from the two 100s.
        var results = new List<ParticipantResult>
        {
            new("p-a", 100, 1, MatchOutcome.Draw),
            new("p-b", 100, 1, MatchOutcome.Draw),
            new("p-c", 50, 3, MatchOutcome.Loss)
        };
        var match = new ArchivedMatch("m1", "CODE01", Language.En, "p-a", "p-b", WinnerId: null, IsDraw: true,
            results, MatchState.Resolved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["q1"], IsLive: true);

        Assert.Equal("draw", match.ToLiveSummary("p-a", Lookup).Outcome);
        Assert.Equal("draw", match.ToLiveSummary("p-b", Lookup).Outcome);
        Assert.Equal("loss", match.ToLiveSummary("p-c", Lookup).Outcome);
    }

    [Fact]
    public void ToLiveSummary_a_sole_winner_reads_win_for_them_and_loss_for_everyone_else()
    {
        var results = new List<ParticipantResult>
        {
            new("p-a", 100, 1, MatchOutcome.Win),
            new("p-b", 80, 2, MatchOutcome.Loss),
            new("p-c", 50, 3, MatchOutcome.Loss)
        };
        var match = new ArchivedMatch("m2", "CODE02", Language.En, "p-a", "p-b", WinnerId: "p-a", IsDraw: false,
            results, MatchState.Resolved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["q1"], IsLive: true);

        Assert.Equal("win", match.ToLiveSummary("p-a", Lookup).Outcome);
        Assert.Equal("loss", match.ToLiveSummary("p-b", Lookup).Outcome);
        Assert.Equal("loss", match.ToLiveSummary("p-c", Lookup).Outcome);
    }

    [Fact]
    public void ToLiveSummary_a_no_contest_reads_pending_before_it_ends_and_draw_for_everyone_once_it_does()
    {
        var stillRunning = new ArchivedMatch("m3", "CODE03", Language.En, "p-a", "p-b", null, false,
            FakeArchive.TestResults("p-a", "p-b", 0, 0), MatchState.AwaitingOpponent, DateTimeOffset.UtcNow, null, ["q1"], IsLive: true);
        Assert.Equal("pending", stillRunning.ToLiveSummary("p-a", Lookup).Outcome);

        // NoContest credits nobody: Results carries only the unranked placeholder (place 0), so this
        // has to be read as its own case rather than as "everyone lost" or "everyone's Outcome field
        // happens to be Loss".
        var noContest = stillRunning with { State = MatchState.NoContest };
        Assert.Equal("draw", noContest.ToLiveSummary("p-a", Lookup).Outcome);
        Assert.Equal("draw", noContest.ToLiveSummary("p-b", Lookup).Outcome);
    }
}
