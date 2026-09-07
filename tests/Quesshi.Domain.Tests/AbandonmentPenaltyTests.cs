using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

/// <summary>
/// The escalating cost of walking away from a live duel: free once, then steeper each time within a
/// rolling 7-day window. <see cref="Player.RecordAbandonment"/> is pure domain — no grain, no store —
/// so these container-free tests only need to prove the number that comes back and what it does to a
/// player's own score; the leaderboard side of the same penalty is covered in Quesshi.Server.Tests,
/// where an actual grain and archive exist to settle a duel against.
/// </summary>
public class AbandonmentPenaltyTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_abandonment_in_a_week_costs_nothing()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);

        var penalty = player.RecordAbandonment(Day0);

        Assert.Equal(0, penalty);
    }

    [Fact]
    public void The_second_third_and_fourth_abandonments_escalate()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);

        Assert.Equal(0, player.RecordAbandonment(Day0));
        Assert.Equal(200, player.RecordAbandonment(Day0.AddHours(1)));
        Assert.Equal(400, player.RecordAbandonment(Day0.AddHours(2)));
        Assert.Equal(800, player.RecordAbandonment(Day0.AddHours(3)));
    }

    [Fact]
    public void A_fifth_abandonment_is_capped_at_the_maximum()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);

        for (var i = 0; i < 4; i++) player.RecordAbandonment(Day0.AddHours(i));
        var fifth = player.RecordAbandonment(Day0.AddHours(4));

        Assert.Equal(LiveRules.AbandonmentPenaltyCap, fifth);
        Assert.Equal(1000, fifth);
    }

    [Fact]
    public void The_window_rolls_so_an_old_abandonment_stops_counting_towards_the_cost()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);

        Assert.Equal(0, player.RecordAbandonment(Day0));
        // Eight days on, the one above has aged out: this is a first offence again, not a second.
        Assert.Equal(0, player.RecordAbandonment(Day0.AddDays(8)));
        // A ninth-day repeat, still within a week of the second, is the second offence.
        Assert.Equal(200, player.RecordAbandonment(Day0.AddDays(9)));
    }

    [Fact]
    public void An_abandonment_exactly_seven_days_old_has_already_rolled_off()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);

        Assert.Equal(0, player.RecordAbandonment(Day0));
        // Exactly one window later: "at > now - window" is false when they're equal, so this one is
        // strictly outside and does not count — a first offence again, not a second.
        Assert.Equal(0, player.RecordAbandonment(Day0 + LiveRules.AbandonmentWindow));
    }

    [Fact]
    public void An_abandonment_a_moment_inside_the_window_still_counts()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);

        Assert.Equal(0, player.RecordAbandonment(Day0));
        // A second short of a full window: still strictly inside, so this is the costly second.
        Assert.Equal(200, player.RecordAbandonment(Day0 + LiveRules.AbandonmentWindow - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_penalty_larger_than_the_players_score_leaves_them_at_exactly_zero()
    {
        var player = Player.Register("p1", "a@example.com", "Amir", Language.En, Day0);
        player.RecordResult(MatchOutcome.Win, 50);

        player.RecordAbandonment(Day0);                 // 1st: free
        player.RecordAbandonment(Day0.AddHours(1));      // 2nd: costs 200, more than the 50 banked

        Assert.Equal(0, player.Stats.TotalScore);
    }
}
