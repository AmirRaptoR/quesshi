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

    [Theory]
    [InlineData(Difficulty.VeryEasy, 80)]    // 160 × 0.5
    [InlineData(Difficulty.Easy, 128)]       // 160 × 0.8
    [InlineData(Difficulty.Medium, 160)]     // 160 × 1.0
    [InlineData(Difficulty.Hard, 208)]       // 160 × 1.3
    [InlineData(Difficulty.VeryHard, 256)]   // 160 × 1.6
    public void A_level_is_worth_what_it_weighs(Difficulty level, int expected)
        => Assert.Equal(expected, Scoring.Score(correct: true, taken: TimeSpan.Zero, limit: Limit, level));

    /// <summary>The reason weighting exists: flat scoring made the easy duel the better duel.</summary>
    [Fact]
    public void A_slow_very_hard_answer_beats_an_instant_very_easy_one()
        => Assert.True(
            Scoring.Score(correct: true, taken: Limit, limit: Limit, Difficulty.VeryHard) >
            Scoring.Score(correct: true, taken: TimeSpan.Zero, limit: Limit, Difficulty.VeryEasy));

    [Fact]
    public void A_wrong_answer_is_worth_nothing_at_any_level()
        => Assert.All(MatchRules.AllLevels,
            level => Assert.Equal(0, Scoring.Score(correct: false, taken: TimeSpan.Zero, limit: Limit, level)));
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
    public void Choosing_levels_narrows_the_ramp_to_them()
    {
        Difficulty[] picked = [Difficulty.Easy, Difficulty.VeryHard];
        var ramp = Enumerable.Range(0, 10).Select(slot => MatchRules.LevelForSlot(slot, 10, picked)).ToList();

        Assert.All(ramp, level => Assert.Contains(level, picked));
        Assert.Equal(Difficulty.Easy, ramp[0]);
        Assert.Equal(Difficulty.VeryHard, ramp[^1]);
        Assert.Equal(ramp.OrderBy(l => l), ramp);
    }

    [Fact]
    public void One_chosen_level_is_the_whole_run()
        => Assert.All(Enumerable.Range(0, 20).Select(slot => MatchRules.LevelForSlot(slot, 20, [Difficulty.Hard])),
            level => Assert.Equal(Difficulty.Hard, level));

    [Fact]
    public void Choosing_nothing_is_the_same_as_choosing_every_level()
        => Assert.Equal(
            Enumerable.Range(0, 12).Select(slot => MatchRules.LevelForSlot(slot, 12)),
            Enumerable.Range(0, 12).Select(slot => MatchRules.LevelForSlot(slot, 12, MatchRules.AllLevels)));

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
