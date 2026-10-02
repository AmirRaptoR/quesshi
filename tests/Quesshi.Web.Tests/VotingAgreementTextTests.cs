using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class VotingAgreementTextTests
{
    [Fact]
    public void Two_or_more_real_answers_with_the_same_option_are_all_agreed()
    {
        var slot = new VotingSlotResultDto(0, [2, 0, 1], AllAgreed: true);

        Assert.Equal(VotingAgreement.AllAgreed,
            VotingAgreementText.Resolve(slot, notApplicableIndex: 2, ownNotApplicable: false));
        Assert.Equal("voting.agreement.allAgreed",
            VotingAgreementText.Key(slot, notApplicableIndex: 2, ownNotApplicable: false));
    }

    [Fact]
    public void Real_answers_that_differ_are_disagreed()
    {
        var slot = new VotingSlotResultDto(0, [1, 1, 1], AllAgreed: false);

        Assert.Equal(VotingAgreement.Disagreed,
            VotingAgreementText.Resolve(slot, notApplicableIndex: 2, ownNotApplicable: false));
        Assert.Equal("voting.agreement.disagreed",
            VotingAgreementText.Key(slot, notApplicableIndex: 2, ownNotApplicable: false));
    }

    [Fact]
    public void Fewer_than_two_real_answers_means_nothing_to_compare()
    {
        var oneReal = new VotingSlotResultDto(0, [1, 0, 3], AllAgreed: true);
        var allNotApplicable = new VotingSlotResultDto(1, [0, 0, 3], AllAgreed: false);

        Assert.Equal(VotingAgreement.NothingToCompare,
            VotingAgreementText.Resolve(oneReal, notApplicableIndex: 2, ownNotApplicable: false));
        Assert.Equal(VotingAgreement.NothingToCompare,
            VotingAgreementText.Resolve(allNotApplicable, notApplicableIndex: 2, ownNotApplicable: false));
    }

    [Fact]
    public void The_viewers_own_not_applicable_answer_forces_nothing_to_compare()
    {
        var everyoneElseAgreed = new VotingSlotResultDto(0, [2, 0, 1], AllAgreed: true);

        Assert.Equal(VotingAgreement.NothingToCompare,
            VotingAgreementText.Resolve(everyoneElseAgreed, notApplicableIndex: 2, ownNotApplicable: true));
        Assert.Equal("voting.agreement.nothingToCompare",
            VotingAgreementText.Key(everyoneElseAgreed, notApplicableIndex: 2, ownNotApplicable: true));
    }

    [Fact]
    public void An_absent_slot_is_also_nothing_to_compare()
        => Assert.Equal(VotingAgreement.NothingToCompare,
            VotingAgreementText.Resolve(null, notApplicableIndex: 2, ownNotApplicable: false));

    [Fact]
    public void The_three_agreement_states_have_distinct_translation_keys()
    {
        var keys = Enum.GetValues<VotingAgreement>().Select(VotingAgreementText.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
