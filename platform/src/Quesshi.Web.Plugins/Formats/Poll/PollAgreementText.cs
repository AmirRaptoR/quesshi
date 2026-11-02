using Quesshi.Formats.Poll;

namespace Quesshi.Web.Plugins.Formats.Poll;

/// <summary>The three messages a closed poll question can show beneath its distribution.</summary>
public enum PollAgreement
{
    AllAgreed,
    Disagreed,
    NothingToCompare
}

/// <summary>
/// Chooses the agreement message without putting answer semantics into Razor. A not-applicable
/// answer is counted in the distribution but is never a meaningful answer for this message.
/// </summary>
public static class PollAgreementText
{
    public static PollAgreement Resolve(PollSlotResult? slot, int notApplicableIndex, bool ownNotApplicable)
    {
        if (slot is null || ownNotApplicable) return PollAgreement.NothingToCompare;

        var counts = slot.Counts ?? [];
        var notApplicable = notApplicableIndex >= 0 && notApplicableIndex < counts.Count
            ? counts[notApplicableIndex]
            : 0;
        var realAnswers = counts.Sum() - notApplicable;
        if (realAnswers < 2) return PollAgreement.NothingToCompare;

        return slot.AllAgreed ? PollAgreement.AllAgreed : PollAgreement.Disagreed;
    }

    public static string Key(PollAgreement agreement) => agreement switch
    {
        PollAgreement.AllAgreed => "voting.agreement.allAgreed",
        PollAgreement.Disagreed => "voting.agreement.disagreed",
        PollAgreement.NothingToCompare => "voting.agreement.nothingToCompare",
        _ => throw new ArgumentOutOfRangeException(nameof(agreement), agreement, null)
    };

    public static string Key(PollSlotResult? slot, int notApplicableIndex, bool ownNotApplicable)
        => Key(Resolve(slot, notApplicableIndex, ownNotApplicable));
}
