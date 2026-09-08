using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Six ways a live duel can end, six distinct headlines. See #13's acceptance criterion: "Resolved
/// (win, loss, draw), abandonment by the opponent, abandonment by you, and no-contest each render a
/// distinct translated headline — six outcomes, six distinct strings."
/// </summary>
public class LiveOutcomeTextTests
{
    [Theory]
    [InlineData(LiveOutcome.Won, "result.win")]
    [InlineData(LiveOutcome.Lost, "result.loss")]
    [InlineData(LiveOutcome.Draw, "result.draw")]
    [InlineData(LiveOutcome.AbandonedByThem, "live.end.abandonedByThem")]
    [InlineData(LiveOutcome.AbandonedByYou, "live.end.abandonedByYou")]
    [InlineData(LiveOutcome.NoContest, "live.end.noContest")]
    public void Each_outcome_maps_to_its_own_key(LiveOutcome outcome, string key)
        => Assert.Equal(key, LiveOutcomeText.HeadlineKey(outcome));

    [Fact]
    public void All_six_outcomes_produce_distinct_keys()
    {
        var keys = Enum.GetValues<LiveOutcome>().Select(LiveOutcomeText.HeadlineKey).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("resolved", "me", false, null, LiveOutcome.Won)]
    [InlineData("resolved", "them", false, null, LiveOutcome.Lost)]
    [InlineData("resolved", null, true, null, LiveOutcome.Draw)]
    [InlineData("abandoned", null, false, "them", LiveOutcome.AbandonedByThem)]
    [InlineData("abandoned", null, false, "me", LiveOutcome.AbandonedByYou)]
    [InlineData("nocontest", null, false, null, LiveOutcome.NoContest)]
    public void Resolve_reads_the_wire_state_from_the_viewers_own_side(
        string state, string? winnerId, bool isDraw, string? abandonedBy, LiveOutcome expected)
        => Assert.Equal(expected, LiveOutcomeText.Resolve(state, winnerId, isDraw, abandonedBy, "me"));
}
