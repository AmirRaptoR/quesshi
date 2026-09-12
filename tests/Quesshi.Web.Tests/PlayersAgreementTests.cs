using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public class PlayersAgreementTests
{
    [Fact]
    public void Everyone_picking_the_same_participant_agrees()
        => Assert.True(PlayersAgreement.AllAgreed([1, 1, 1]));

    [Fact]
    public void Different_picks_do_not_agree()
        => Assert.False(PlayersAgreement.AllAgreed([1, 0, 1]));

    [Fact]
    public void A_miss_is_ignored_rather_than_counted_as_a_disagreement()
        => Assert.True(PlayersAgreement.AllAgreed([1, 1, -1]));

    [Fact]
    public void Fewer_than_two_answers_is_not_agreement()
    {
        Assert.False(PlayersAgreement.AllAgreed([1]));
        Assert.False(PlayersAgreement.AllAgreed([]));
        Assert.False(PlayersAgreement.AllAgreed([-1, -1]));
    }
}
