using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class ScoringTests
{
    private static readonly TimeSpan Limit = MatchRules.QuestionTime;

    [Fact]
    public void Full_credit_preserves_the_instant_answer_score()
        => Assert.Equal(160, Scoring.Score(credit: 1, taken: TimeSpan.Zero, limit: Limit));

    [Fact]
    public void Full_credit_at_the_buzzer_scores_the_base_amount()
        => Assert.Equal(100, Scoring.Score(credit: 1, taken: Limit, limit: Limit));

    [Fact]
    public void Full_credit_halfway_through_scores_half_the_speed_bonus()
        => Assert.Equal(130, Scoring.Score(credit: 1, taken: Limit / 2, limit: Limit));

    [Fact]
    public void Zero_credit_scores_nothing_however_fast()
        => Assert.Equal(0, Scoring.Score(credit: 0, taken: TimeSpan.Zero, limit: Limit));

    [Fact]
    public void Half_credit_is_applied_before_rounding()
        => Assert.Equal(84, Scoring.Score(credit: 0.5, taken: Limit / 2, limit: Limit, level: 4));

    [Fact]
    public void A_missing_level_uses_medium_weight()
        => Assert.Equal(160, Scoring.Score(credit: 1, taken: TimeSpan.Zero, limit: Limit, level: null));

    [Theory]
    [InlineData(1, 80)]    // 160 × 0.5
    [InlineData(2, 128)]   // 160 × 0.8
    [InlineData(3, 160)]   // 160 × 1.0
    [InlineData(4, 208)]   // 160 × 1.3
    [InlineData(5, 256)]   // 160 × 1.6
    public void A_level_is_worth_what_it_weighs(int level, int expected)
        => Assert.Equal(expected, Scoring.Score(credit: 1, taken: TimeSpan.Zero, limit: Limit, level));

    /// <summary>The reason weighting exists: hard questions should reward their difficulty.</summary>
    [Fact]
    public void A_slow_level_five_answer_beats_an_instant_level_one_answer()
        => Assert.True(
            Scoring.Score(credit: 1, taken: Limit, limit: Limit, level: 5) >
            Scoring.Score(credit: 1, taken: TimeSpan.Zero, limit: Limit, level: 1));

    [Fact]
    public void Zero_credit_scores_nothing_at_any_level()
    {
        foreach (var level in Enumerable.Range(1, 5))
        {
            Assert.Equal(0, Scoring.Score(credit: 0, taken: TimeSpan.Zero, limit: Limit, level));
        }
    }

    [Fact]
    public void Answer_after_the_network_grace_period_scores_nothing()
        => Assert.Equal(0, Scoring.Score(credit: 1, taken: Limit + MatchRules.NetworkGrace + TimeSpan.FromTicks(1), limit: Limit));

    [Fact]
    public void Answer_inside_the_network_grace_period_retains_the_buzzer_score()
        => Assert.Equal(100, Scoring.Score(credit: 1, taken: Limit + MatchRules.NetworkGrace, limit: Limit));

    [Fact]
    public void Negative_taken_time_retains_the_full_speed_bonus()
        => Assert.Equal(160, Scoring.Score(credit: 1, taken: -TimeSpan.FromSeconds(1), limit: Limit));
}
