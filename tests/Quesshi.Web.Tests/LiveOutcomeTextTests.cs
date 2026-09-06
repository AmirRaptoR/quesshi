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
}
