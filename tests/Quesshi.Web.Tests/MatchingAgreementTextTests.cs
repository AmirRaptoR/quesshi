using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class MatchingAgreementTextTests
{
    [Fact]
    public void Two_or_more_real_answers_with_the_same_option_are_all_agreed()
    {
        var slot = new MatchingSlotResultDto(0, [2, 0, 1], AllAgreed: true);

        Assert.Equal(MatchingAgreement.AllAgreed,
            MatchingAgreementText.Resolve(slot, notApplicableIndex: 2, ownNotApplicable: false));
        Assert.Equal("matching.agreement.allAgreed",
            MatchingAgreementText.Key(slot, notApplicableIndex: 2, ownNotApplicable: false));
    }

    [Fact]
    public void Real_answers_that_differ_are_disagreed()
    {
        var slot = new MatchingSlotResultDto(0, [1, 1, 1], AllAgreed: false);

        Assert.Equal(MatchingAgreement.Disagreed,
            MatchingAgreementText.Resolve(slot, notApplicableIndex: 2, ownNotApplicable: false));
        Assert.Equal("matching.agreement.disagreed",
            MatchingAgreementText.Key(slot, notApplicableIndex: 2, ownNotApplicable: false));
    }

    [Fact]
    public void Fewer_than_two_real_answers_means_nothing_to_compare()
    {
        var oneReal = new MatchingSlotResultDto(0, [1, 0, 3], AllAgreed: true);
        var allNotApplicable = new MatchingSlotResultDto(1, [0, 0, 3], AllAgreed: false);

        Assert.Equal(MatchingAgreement.NothingToCompare,
            MatchingAgreementText.Resolve(oneReal, notApplicableIndex: 2, ownNotApplicable: false));
        Assert.Equal(MatchingAgreement.NothingToCompare,
            MatchingAgreementText.Resolve(allNotApplicable, notApplicableIndex: 2, ownNotApplicable: false));
    }

    [Fact]
    public void The_viewers_own_not_applicable_answer_forces_nothing_to_compare()
    {
        var everyoneElseAgreed = new MatchingSlotResultDto(0, [2, 0, 1], AllAgreed: true);

        Assert.Equal(MatchingAgreement.NothingToCompare,
            MatchingAgreementText.Resolve(everyoneElseAgreed, notApplicableIndex: 2, ownNotApplicable: true));
        Assert.Equal("matching.agreement.nothingToCompare",
            MatchingAgreementText.Key(everyoneElseAgreed, notApplicableIndex: 2, ownNotApplicable: true));
    }

    [Fact]
    public void An_absent_slot_is_also_nothing_to_compare()
        => Assert.Equal(MatchingAgreement.NothingToCompare,
            MatchingAgreementText.Resolve(null, notApplicableIndex: 2, ownNotApplicable: false));

    [Fact]
    public void The_three_agreement_states_have_distinct_translation_keys()
    {
        var keys = Enum.GetValues<MatchingAgreement>().Select(MatchingAgreementText.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
