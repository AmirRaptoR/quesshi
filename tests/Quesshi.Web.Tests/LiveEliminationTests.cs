using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Issue #53's eliminated/spectating state: a player who has missed <c>LiveRules.MissesBeforeAbandon</c>
/// (3) rounds in a row stops being offered answer buttons and the survivors stop waiting on their
/// seat. <see cref="LiveElimination"/> is the pure derivation both <c>Live.razor</c> and
/// <c>DuelBar.razor</c> read to decide that, so it is what this covers — see the class's own remarks
/// for why a plain <c>MissStreak</c> threshold is reliable across a reload or a rejoin, not just for
/// the live <c>PlayerEliminated</c> push in the moment.
/// </summary>
public class LiveEliminationTests
{
    private static LivePlayerViewDto Player(string id, int missStreak) => new(id, 0, 0, missStreak);

    [Fact]
    public void A_player_below_the_threshold_is_not_eliminated()
    {
        Assert.False(LiveElimination.IsEliminated(Player("amir", LiveElimination.MissesBeforeAbandon - 1)));
    }

    [Fact]
    public void A_player_who_has_hit_the_threshold_is_eliminated()
    {
        Assert.True(LiveElimination.IsEliminated(Player("amir", LiveElimination.MissesBeforeAbandon)));
    }

    /// <summary>
    /// A three-player duel where only the third seat has dropped: the eliminated set names exactly
    /// them, so the survivors' "everyone has answered" check can stop waiting on that one seat
    /// without mistaking either of themselves for the one who is out.
    /// </summary>
    [Fact]
    public void Only_the_players_at_or_past_the_streak_are_eliminated()
    {
        List<LivePlayerViewDto> players = [Player("amir", 0), Player("sara", 2), Player("reza", 3)];
        var view = new LiveViewDto(
            "m1", [new("amir", "Amir", "a", false), new("sara", "Sara", "s", false), new("reza", "Reza", "r", false)],
            "inprogress", "question", null, DateTimeOffset.UtcNow, 1, 5,
            players, [], [], null, false, [], DateTimeOffset.UtcNow, null);

        Assert.Equal(["reza"], LiveElimination.EliminatedIds(view));
    }

    /// <summary>
    /// Once dropped, a player's streak is never touched again by <c>LiveMatch.CloseRound</c> (they
    /// have left the active set for good) — a streak sitting well past the threshold from an earlier
    /// round must still read as eliminated on a fresh load, not just the instant it first crossed it.
    /// </summary>
    [Fact]
    public void A_streak_left_stranded_above_the_threshold_still_reads_as_eliminated()
    {
        Assert.True(LiveElimination.IsEliminated(Player("amir", LiveElimination.MissesBeforeAbandon + 5)));
    }
}
