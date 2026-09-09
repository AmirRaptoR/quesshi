using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// <see cref="SoundOutcomeMapping"/> reads the same lowercase "win"/"draw"/"loss" (and, for an async
/// duel still awaiting the other side, "pending") convention every outcome string on this API already
/// uses — see <see cref="StandingRowDto"/> and <c>MatchSummaryDto</c>'s own remarks.
/// </summary>
public class SoundOutcomeTests
{
    private static StandingRowDto Standing(string outcome) => new("p1", "Player One", "fox", Score: 10, Place: 1, Outcome: outcome);

    [Fact]
    public void A_won_standing_plays_the_win_cue()
        => Assert.Equal(SoundOutcome.Win, SoundOutcomeMapping.ForOutcome(Standing("win").Outcome));

    [Fact]
    public void A_lost_standing_plays_the_loss_cue()
        => Assert.Equal(SoundOutcome.Loss, SoundOutcomeMapping.ForOutcome(Standing("loss").Outcome));

    /// <summary>Neither cue for a draw — a tie is not quite a win, and the loss buzz would be worse
    /// than nothing.</summary>
    [Fact]
    public void A_drawn_standing_plays_neither_cue()
        => Assert.Equal(SoundOutcome.None, SoundOutcomeMapping.ForOutcome(Standing("draw").Outcome));

    /// <summary>An async duel's own run finishing does not mean the duel has resolved for this player
    /// — Api.MatchAsync's Outcome comes back "pending" while the opponent is still mid-run, and that
    /// is not a loss any more than it is a win.</summary>
    [Fact]
    public void A_pending_async_duel_plays_neither_cue()
        => Assert.Equal(SoundOutcome.None, SoundOutcomeMapping.ForOutcome("pending"));
}
