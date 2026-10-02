using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Small, testable wording decisions for the voting final screen.</summary>
public static class VotingResultsText
{
    /// <summary>
    /// A pair with no comparable real answers has no percentage to display; it must not be rendered
    /// as 0%, which would claim disagreement where there was simply nothing to compare.
    /// </summary>
    public static string PairAgreementKey(VotingPairStatDto pair)
        => pair.AgreementPercent is null
            ? "voting.results.nothingToCompare"
            : "voting.results.agreementPercent";

    public static string PairAgreement(VotingPairStatDto pair, Translator translator)
        => pair.AgreementPercent is { } percent
            ? translator.Format(PairAgreementKey(pair), translator.Num(percent))
            : translator[PairAgreementKey(pair)];

    public static string NoContestKey => "voting.results.noContest";

    public static string NoContest(Translator translator) => translator[NoContestKey];
}
