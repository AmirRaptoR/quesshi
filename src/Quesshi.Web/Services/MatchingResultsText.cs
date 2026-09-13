using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Small, testable wording decisions for the matching final screen.</summary>
public static class MatchingResultsText
{
    /// <summary>
    /// A pair with no comparable real answers has no percentage to display; it must not be rendered
    /// as 0%, which would claim disagreement where there was simply nothing to compare.
    /// </summary>
    public static string PairAgreementKey(MatchingPairStatDto pair)
        => pair.AgreementPercent is null
            ? "matching.results.nothingToCompare"
            : "matching.results.agreementPercent";

    public static string PairAgreement(MatchingPairStatDto pair, Translator translator)
        => pair.AgreementPercent is { } percent
            ? translator.Format(PairAgreementKey(pair), translator.Num(percent))
            : translator[PairAgreementKey(pair)];

    public static string NoContestKey => "matching.results.noContest";

    public static string NoContest(Translator translator) => translator[NoContestKey];
}
