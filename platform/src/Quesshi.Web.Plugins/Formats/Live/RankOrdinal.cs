namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>
/// Writes a finishing place out as an ordinal in each of the app's three languages. Pure — no HTTP, no
/// translator instance — so a plain xunit test exercises the numeral-and-suffix shape directly.
/// </summary>
public static class RankOrdinal
{
    /// <summary>
    /// <paramref name="localisedRank"/> is the rank's digits exactly as the viewer's numerals already render
    /// them — this only appends the ordinal marker after them, so digit localisation stays the one thing
    /// the translator owns. <paramref name="rank"/>, the plain int, decides *which* marker: English's
    /// -st/-nd/-rd/-th depends on the number itself (11th, not 11st).
    ///
    /// Persian and Dutch each use one marker for every rank rather than English's four: Persian's "م" and
    /// Dutch's "e".
    /// </summary>
    public static string Format(string localisedRank, int rank, string lang) => lang switch
    {
        "nl" => $"{localisedRank}e",
        "fa" => $"{localisedRank}م",
        _ => localisedRank + EnglishSuffix(rank)
    };

    private static string EnglishSuffix(int rank) => rank % 100 is >= 11 and <= 13
        ? "th"
        : (rank % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
}
