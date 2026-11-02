using Quesshi.Formats.Poll;

namespace Quesshi.Web.Plugins.Formats.Poll;

/// <summary>Small, testable wording decisions for the finished poll.</summary>
public static class PollResultsText
{
    /// <summary>
    /// A pair with no comparable real answers has no percentage to display; it must not be rendered
    /// as 0%, which would claim disagreement where there was simply nothing to compare.
    /// </summary>
    public static string PairAgreementKey(PollPairStat pair)
        => pair.AgreementPercent is null
            ? "voting.results.nothingToCompare"
            : "voting.results.agreementPercent";

    public static string PairAgreement(PollPairStat pair, IClientTranslator translator)
        => pair.AgreementPercent is { } percent
            ? string.Format(translator[PairAgreementKey(pair)], translator.Num(percent))
            : translator[PairAgreementKey(pair)];

    public static string NoContestKey => "voting.results.noContest";

    public static string NoContest(IClientTranslator translator) => translator[NoContestKey];
}
