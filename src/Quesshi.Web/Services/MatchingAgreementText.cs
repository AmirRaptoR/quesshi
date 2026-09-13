using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>The three messages a closed matching slot can show beneath its distribution.</summary>
public enum MatchingAgreement
{
    AllAgreed,
    Disagreed,
    NothingToCompare
}

/// <summary>
/// Chooses the matching agreement message without putting answer semantics into Razor. A
/// not-applicable answer is a real served option and can be counted in the distribution, but it is
/// never a meaningful answer for this message — especially when it is the viewer's own answer.
/// </summary>
public static class MatchingAgreementText
{
    public static MatchingAgreement Resolve(MatchingSlotResultDto? slot, int notApplicableIndex,
        bool ownNotApplicable)
    {
        if (slot is null || ownNotApplicable) return MatchingAgreement.NothingToCompare;

        var counts = slot.Counts ?? [];
        var notApplicable = notApplicableIndex >= 0 && notApplicableIndex < counts.Count
            ? counts[notApplicableIndex]
            : 0;
        var realAnswers = counts.Sum() - notApplicable;
        if (realAnswers < 2) return MatchingAgreement.NothingToCompare;

        return slot.AllAgreed ? MatchingAgreement.AllAgreed : MatchingAgreement.Disagreed;
    }

    public static string Key(MatchingAgreement agreement) => agreement switch
    {
        MatchingAgreement.AllAgreed => "matching.agreement.allAgreed",
        MatchingAgreement.Disagreed => "matching.agreement.disagreed",
        MatchingAgreement.NothingToCompare => "matching.agreement.nothingToCompare",
        _ => throw new ArgumentOutOfRangeException(nameof(agreement), agreement, null)
    };

    public static string Key(MatchingSlotResultDto? slot, int notApplicableIndex,
        bool ownNotApplicable)
        => Key(Resolve(slot, notApplicableIndex, ownNotApplicable));
}
