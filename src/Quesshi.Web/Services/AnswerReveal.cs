using System.Globalization;

namespace Quesshi.Web.Services;

/// <summary>
/// One place in a revealed sorting question: what belonged there, and what somebody put there.
/// </summary>
/// <param name="Place">Zero-based; the screens number it from one.</param>
/// <param name="Correct">The item that belongs in this place.</param>
/// <param name="Placed">
/// The item that was actually put here, or null if this player never answered — a timed-out round
/// has a correct order to show and nothing to show beside it.
/// </param>
/// <param name="Right">Whether those two are the same item, which is what gets coloured.</param>
public sealed record SortRevealRow(int Place, string Correct, string? Placed, bool Right);

/// <summary>
/// Turns the answer fields on the reveal contracts into something a screen can draw. Pulled out of
/// the markup for the same reason <c>StandingsPresentation</c> and <c>LivePhaseSelector</c> were:
/// this project has no bUnit, so a mapping that lives in a <c>.razor</c> file is a mapping with no
/// test.
/// </summary>
public static class AnswerReveal
{
    /// <summary>
    /// The correct order beside somebody's, place by place.
    /// <para>
    /// Both sides arrive as <b>text</b>, which is what lets one component render every path into
    /// this reveal. A live or async reveal gets the other player's order by way of
    /// <see cref="PlacedItems"/>, which resolves the stored-index response the server sent; the
    /// answering screen shows the player their own order straight from the items the card served
    /// them, with no response string involved at all, because at that moment the client has not been
    /// told what the server normalised its submission into.
    /// </para>
    /// <para>
    /// Rightness is therefore "is this the same item", not "is this the same index". That is exact
    /// rather than approximate: a sorting question's four items are validated distinct, so two
    /// places can never hold the same text.
    /// </para>
    /// </summary>
    public static List<SortRevealRow> SortRows(IReadOnlyList<string>? correctOrder, IReadOnlyList<string>? placed)
    {
        if (correctOrder is null || correctOrder.Count == 0) return [];

        // A partial order is no order: rather than render half a column and leave the rest blank —
        // which would read as "they answered these three and gave up" — an order that is not the
        // right length is treated as the absent answer it is.
        var answer = placed is not null && placed.Count == correctOrder.Count ? placed : null;

        return [.. correctOrder.Select((item, place) => new SortRevealRow(
            place,
            item,
            answer?[place],
            answer is not null && answer[place] == item))];
    }

    /// <summary>
    /// A stored-index response — <c>"2,0,3,1"</c> — as the items it names, in the order it places
    /// them. Null for an answer that is absent or unreadable, which a reveal renders as an empty
    /// column rather than as no reveal at all: one player's malformed answer must not blank out the
    /// round for everybody.
    /// <para>
    /// This is a lookup and never a shuffle, and that is the whole payoff of the grain normalising a
    /// sort answer at submission (see <c>SortOrder.ToStoredOrder</c>). Stored order <i>is</i> correct
    /// order, so <c>correctOrder[n]</c> is the item stored at index <c>n</c>, and nothing here needs
    /// the round's shuffle seed. If it ever appeared to, the answer would have been stored in the
    /// wrong space — a bug to report, not to compensate for here.
    /// </para>
    /// </summary>
    public static List<string>? PlacedItems(IReadOnlyList<string>? correctOrder, string? response)
    {
        if (correctOrder is null || correctOrder.Count == 0) return null;

        var order = ParseOrder(response, correctOrder.Count);
        return order is null ? null : [.. order.Select(index => correctOrder[index])];
    }

    /// <summary>
    /// Whether a player's answer to a revealed question was right, or null when this side cannot
    /// tell.
    /// <para>
    /// It can tell for two of the three kinds. A <c>Choice</c> answer is right when the index matches
    /// the revealed one, and a <c>Sort</c> answer is right when the stored-index order it was
    /// normalised into is the identity — <c>"0,1,2,3"</c> — because stored order is correct order. A
    /// <c>Map</c> answer it can tell only for a country, which is a string comparison; a city is
    /// "within the question's tolerance radius", and <b>no reveal contract carries that radius</b>.
    /// Rather than guess, this returns null there and the caller shows the question as unresolved
    /// instead of asserting a verdict it does not have.
    /// </para>
    /// </summary>
    public static bool? IsRight(int kind, int correctIndex, string? correctTarget, int? choiceIndex, string? response)
        => kind switch
        {
            1 => response is null ? null : ParseOrder(response, CountOf(response)) is { } order && IsIdentity(order),
            2 => MapPick.Parse(correctTarget) is { IsCountry: true } target && MapPick.Parse(response) is { IsCountry: true } pick
                ? string.Equals(target.CountryCode, pick.CountryCode, StringComparison.Ordinal)
                : null,
            _ => choiceIndex is null ? null : choiceIndex == correctIndex
        };

    /// <summary>
    /// Reads <c>"2,0,3,1"</c> into indices, and only accepts a genuine permutation of
    /// <c>0..count-1</c>. The mirror of the server's <c>SortOrder.TryParseOrder</c>, and invariant
    /// for the same reason: a Persian browser must not be able to produce — or here, to accept —
    /// Persian digits.
    /// </summary>
    private static int[]? ParseOrder(string? response, int count)
    {
        if (string.IsNullOrWhiteSpace(response) || count <= 0) return null;

        var parts = response.Split(',');
        if (parts.Length != count) return null;

        var order = new int[count];
        var seen = new bool[count];

        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return null;
            if (index < 0 || index >= count || seen[index]) return null;

            seen[index] = true;
            order[i] = index;
        }

        return order;
    }

    private static int CountOf(string response) => response.Split(',').Length;

    private static bool IsIdentity(int[] order)
    {
        for (var i = 0; i < order.Length; i++)
            if (order[i] != i) return false;

        return true;
    }
}
