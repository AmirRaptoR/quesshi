using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Profile.razor's "1,215 points · 2nd overall" line needs two things no existing type gave it:
/// where the signed-in player sits on <c>GET /api/leaderboard</c>'s own top-20 (<see cref="Rank"/>),
/// and how to write that position out as an ordinal in each of the app's three languages
/// (<see cref="Format"/>). Both are pure — no HTTP, no <c>Translator</c> instance — so a plain xunit
/// test exercises the numeral-and-suffix shape directly, no rendered page or running server required.
/// </summary>
public static class RankOrdinal
{
    /// <summary>
    /// A player who never cracked the top 20 is not "rank 21" or some estimate — the leaderboard
    /// endpoint never told anyone their real position past the cut, so the honest answer is "no rank
    /// to show", which is exactly what a missing row here means. Profile.razor renders "1,215 points"
    /// alone in that case rather than inventing an "overall" clause with nothing behind it.
    /// </summary>
    public static int? Rank(IReadOnlyList<LeaderboardRowDto> leaderboard, string meId)
        => leaderboard.FirstOrDefault(r => r.PlayerId == meId)?.Rank;

    /// <summary>
    /// <paramref name="localisedRank"/> is the rank's digits exactly as <c>Translator.Num</c> already
    /// renders them (Persian digits swapped in, everyone else left alone) — this only ever appends the
    /// ordinal marker after them, so digit localisation stays the one thing <c>Translator</c> owns
    /// rather than being duplicated here. <paramref name="rank"/>, the plain int, is what decides
    /// *which* marker: English's -st/-nd/-rd/-th depends on the number itself (11th, not 11st), which
    /// a string can no longer answer once its own digits might already have been swapped to Persian.
    ///
    /// Persian and Dutch each use one marker for every rank rather than English's four: Persian's "م"
    /// (the same digit+"م" shorthand Persian apps reach for instead of spelling out "دوم", "سوم" for
    /// every number) and Dutch's "e" (colloquial for every ordinal from "1e" to "100e" — the formal
    /// "-ste"/"-de" split exists but nobody writes a leaderboard that way).
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
