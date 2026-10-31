using Quesshi.Features.Leaderboard;

namespace Quesshi.Features.Tests.Leaderboard;

public class AbandonmentRulesTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(4, 800)]
    [InlineData(5, 1000)]
    [InlineData(6, 1000)]
    [InlineData(40, 1000)]
    [InlineData(int.MaxValue, 1000)]
    public void Penalty_follows_the_fixed_tiers(int occurrence, int expected)
        => Assert.Equal(expected, AbandonmentRules.Penalty(occurrence));

    [Fact]
    public void Window_is_seven_days() => Assert.Equal(TimeSpan.FromDays(7), AbandonmentRules.Window);

    [Fact]
    public void The_first_abandonment_in_a_week_costs_nothing()
        => Assert.Equal(0, new AbandonmentHistory().RecordAbandonment(Day0));

    [Fact]
    public void The_second_third_and_fourth_abandonments_escalate()
    {
        var history = new AbandonmentHistory();

        Assert.Equal(0, history.RecordAbandonment(Day0));
        Assert.Equal(200, history.RecordAbandonment(Day0.AddHours(1)));
        Assert.Equal(400, history.RecordAbandonment(Day0.AddHours(2)));
        Assert.Equal(800, history.RecordAbandonment(Day0.AddHours(3)));
    }

    [Fact]
    public void A_fifth_abandonment_is_capped_at_the_maximum()
    {
        var history = new AbandonmentHistory();

        for (var i = 0; i < 4; i++) history.RecordAbandonment(Day0.AddHours(i));

        Assert.Equal(1000, history.RecordAbandonment(Day0.AddHours(4)));
    }

    [Fact]
    public void The_window_rolls_so_an_old_abandonment_stops_counting()
    {
        var history = new AbandonmentHistory();

        Assert.Equal(0, history.RecordAbandonment(Day0));
        Assert.Equal(0, history.RecordAbandonment(Day0.AddDays(8)));
        Assert.Equal(200, history.RecordAbandonment(Day0.AddDays(9)));
    }

    [Fact]
    public void An_abandonment_exactly_seven_days_old_has_already_rolled_off()
    {
        var history = new AbandonmentHistory();

        Assert.Equal(0, history.RecordAbandonment(Day0));
        Assert.Equal(0, history.RecordAbandonment(Day0 + AbandonmentRules.Window));
    }

    [Fact]
    public void An_abandonment_a_moment_inside_the_window_still_counts()
    {
        var history = new AbandonmentHistory();

        Assert.Equal(0, history.RecordAbandonment(Day0));
        Assert.Equal(200, history.RecordAbandonment(Day0 + AbandonmentRules.Window - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_penalty_larger_than_the_score_leaves_exactly_zero()
    {
        var history = new AbandonmentHistory();
        history.AddPoints(50);

        history.RecordAbandonment(Day0);
        history.RecordAbandonment(Day0.AddHours(1));

        Assert.Equal(0, history.Score);
    }

    [Fact]
    public void An_abandonment_settled_before_an_earlier_one_computes_its_tier_from_its_own_window_only()
    {
        var history = new AbandonmentHistory();

        Assert.Equal(0, history.RecordAbandonment(Day0.AddDays(2)));
        Assert.Equal(0, history.RecordAbandonment(Day0));
        Assert.Equal([Day0, Day0.AddDays(2)], history.Abandonments);
    }

    [Fact]
    public void A_later_abandonment_still_counts_every_out_of_order_entry_on_record()
    {
        var history = new AbandonmentHistory();

        history.RecordAbandonment(Day0.AddDays(2));
        history.RecordAbandonment(Day0);

        Assert.Equal(400, history.RecordAbandonment(Day0.AddDays(3)));
    }
}
