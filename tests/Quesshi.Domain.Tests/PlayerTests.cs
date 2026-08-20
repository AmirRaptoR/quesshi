using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class PlayerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static Player New() => Player.Register("p1", "a@b.com", "Amir", Language.Fa, T0);

    [Fact]
    public void A_new_player_starts_empty()
    {
        var p = New();
        Assert.Equal(0, p.Stats.Played);
        Assert.Equal(0, p.Stats.Streak);
    }

    [Fact]
    public void Wins_build_a_streak_and_a_loss_breaks_it()
    {
        var p = New();
        p.RecordResult(MatchOutcome.Win);
        p.RecordResult(MatchOutcome.Win);
        Assert.Equal(2, p.Stats.Streak);
        Assert.Equal(2, p.Stats.BestStreak);

        p.RecordResult(MatchOutcome.Loss);
        Assert.Equal(0, p.Stats.Streak);
        Assert.Equal(2, p.Stats.BestStreak);
        Assert.Equal(3, p.Stats.Played);
    }

    [Fact]
    public void A_draw_keeps_the_streak_but_does_not_extend_it()
    {
        var p = New();
        p.RecordResult(MatchOutcome.Win);
        p.RecordResult(MatchOutcome.Draw);
        Assert.Equal(1, p.Stats.Streak);
        Assert.Equal(1, p.Stats.Draws);
    }

    [Fact]
    public void Per_category_accuracy_is_tracked()
    {
        var p = New();
        p.RecordAnswer("geography", correct: true);
        p.RecordAnswer("geography", correct: false);
        p.RecordAnswer("movies", correct: true);

        Assert.Equal(0.5, p.Accuracy("geography"));
        Assert.Equal(1.0, p.Accuracy("movies"));
        Assert.Equal(0.0, p.Accuracy("never-played"));
    }

    [Fact]
    public void Befriending_is_idempotent_and_never_self_directed()
    {
        var p = New();
        p.AddFriend("p2");
        p.AddFriend("p2");
        p.AddFriend("p1");
        Assert.Equal(["p2"], p.Friends);
    }

    [Fact]
    public void A_guest_is_marked_as_one_and_owns_no_reachable_address()
    {
        var guest = Player.Guest("g1", "  Sara  ", Language.Fa, T0);

        Assert.True(guest.IsGuest);
        Assert.Equal("Sara", guest.DisplayName);

        // .invalid is reserved precisely so it can never resolve, which is the point: the address
        // exists to occupy a slot in a unique index, not to be written to.
        Assert.EndsWith("@guest.invalid", guest.Email);
    }

    [Fact]
    public void Two_guests_never_share_an_address()
        => Assert.NotEqual(Player.Guest("g1", "a", Language.Fa, T0).Email,
                           Player.Guest("g2", "b", Language.Fa, T0).Email);

    [Fact]
    public void A_registered_player_is_not_a_guest()
        => Assert.False(Player.Register("p1", "someone@example.com", "Someone", Language.En, T0).IsGuest);

    [Fact]
    public void Being_a_guest_survives_a_snapshot_round_trip()
    {
        var guest = Player.Guest("g1", "Sara", Language.Fa, T0);
        guest.RecordResult(MatchOutcome.Win, 480);

        var restored = Player.FromSnapshot(guest.ToSnapshot());

        Assert.True(restored.IsGuest);
        Assert.Equal(480, restored.Stats.TotalScore);
    }

    /// <summary>Player documents written before guests existed carry no flag, and must read as accounts.</summary>
    [Fact]
    public void A_snapshot_with_no_guest_flag_restores_as_a_full_account()
    {
        var snapshot = new PlayerSnapshot("p1", "someone@example.com", "Someone", "p1", Language.En, false,
            T0, PlayerStats.Empty, [], []);

        Assert.False(Player.FromSnapshot(snapshot).IsGuest);
    }
}
