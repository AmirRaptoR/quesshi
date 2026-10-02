using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>The three messages a closed voting slot can show beneath its distribution.</summary>
public enum VotingAgreement
{
    AllAgreed,
    Disagreed,
    NothingToCompare
}

/// <summary>
/// Chooses the voting agreement message without putting answer semantics into Razor. A
/// not-applicable answer is a real served option and can be counted in the distribution, but it is
/// never a meaningful answer for this message — especially when it is the viewer's own answer.
/// </summary>
public static class VotingAgreementText
{
    public static VotingAgreement Resolve(VotingSlotResultDto? slot, int notApplicableIndex,
        bool ownNotApplicable)
    {
        if (slot is null || ownNotApplicable) return VotingAgreement.NothingToCompare;

        var counts = slot.Counts ?? [];
        var notApplicable = notApplicableIndex >= 0 && notApplicableIndex < counts.Count
            ? counts[notApplicableIndex]
            : 0;
        var realAnswers = counts.Sum() - notApplicable;
        if (realAnswers < 2) return VotingAgreement.NothingToCompare;

        return slot.AllAgreed ? VotingAgreement.AllAgreed : VotingAgreement.Disagreed;
    }

    public static string Key(VotingAgreement agreement) => agreement switch
    {
        VotingAgreement.AllAgreed => "voting.agreement.allAgreed",
        VotingAgreement.Disagreed => "voting.agreement.disagreed",
        VotingAgreement.NothingToCompare => "voting.agreement.nothingToCompare",
        _ => throw new ArgumentOutOfRangeException(nameof(agreement), agreement, null)
    };

    public static string Key(VotingSlotResultDto? slot, int notApplicableIndex,
        bool ownNotApplicable)
        => Key(Resolve(slot, notApplicableIndex, ownNotApplicable));
}
