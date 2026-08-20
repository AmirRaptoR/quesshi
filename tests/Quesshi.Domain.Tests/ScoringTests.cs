using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class ScoringTests
{
    private static readonly TimeSpan Limit = MatchRules.QuestionTime;

    [Fact]
    public void Instant_correct_answer_scores_base_plus_full_speed_bonus()
        => Assert.Equal(160, Scoring.Score(correct: true, taken: TimeSpan.Zero, limit: Limit));

    [Fact]
    public void Correct_answer_at_the_buzzer_scores_base_only()
        => Assert.Equal(100, Scoring.Score(correct: true, taken: Limit, limit: Limit));

    [Fact]
    public void Correct_answer_halfway_scores_half_the_speed_bonus()
        => Assert.Equal(130, Scoring.Score(correct: true, taken: Limit / 2, limit: Limit));

    [Fact]
    public void Wrong_answer_scores_nothing_however_fast()
        => Assert.Equal(0, Scoring.Score(correct: false, taken: TimeSpan.Zero, limit: Limit));

    [Fact]
    public void Correct_answer_arriving_after_the_limit_scores_nothing()
        => Assert.Equal(0, Scoring.Score(correct: true, taken: Limit + TimeSpan.FromSeconds(5), limit: Limit));

    [Fact]
    public void Answer_inside_the_network_grace_period_still_counts()
        => Assert.True(Scoring.Score(correct: true, taken: Limit + TimeSpan.FromSeconds(1), limit: Limit) > 0);
}

public class DifficultyRampTests
{
    [Fact]
    public void A_match_opens_at_the_bottom_and_finishes_at_the_top()
    {
        var ramp = Enumerable.Range(0, MatchRules.QuestionsPerMatch).Select(slot => MatchRules.LevelForSlot(slot)).ToList();

        Assert.Equal(Difficulty.VeryEasy, ramp[0]);
        Assert.Equal(Difficulty.VeryHard, ramp[^1]);
    }

    [Fact]
    public void The_ramp_never_goes_backwards()
    {
        var ramp = Enumerable.Range(0, MatchRules.QuestionsPerMatch).Select(slot => MatchRules.LevelForSlot(slot)).ToList();
        Assert.Equal(ramp.OrderBy(l => l), ramp);
    }

    [Fact]
    public void Every_level_is_a_real_one()
        => Assert.All(Enumerable.Range(0, MatchRules.QuestionsPerMatch).Select(slot => MatchRules.LevelForSlot(slot)),
            level => Assert.Contains(level, MatchRules.AllLevels));

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void The_ramp_stretches_to_any_match_length(int total)
    {
        var ramp = Enumerable.Range(0, total).Select(slot => MatchRules.LevelForSlot(slot, total)).ToList();

        Assert.Equal(Difficulty.VeryEasy, ramp[0]);
        Assert.Equal(Difficulty.VeryHard, ramp[^1]);
        Assert.Equal(ramp, ramp.Order());
    }
}
