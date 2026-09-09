using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Putting a pick into words: the naming step that makes a world map answerable on a phone with no
/// pan and no zoom. A player finds out they hit Belgium instead of the Netherlands <i>before</i> the
/// buzzer rather than after, which is the entire justification for the bar beneath the map.
/// <para>
/// Kept out of the components so both the picking bar and the reveal's legend say the same thing
/// about the same pick, and so what they say is tested.
/// </para>
/// </summary>
public static class MapText
{
    /// <summary>
    /// A country's name as the map itself spells it, falling back to the bare ISO code for a code
    /// the map has no path for.
    /// <para>
    /// The name is English in all three languages, and that is a known, documented shortfall rather
    /// than an oversight — see <see cref="MapCountry.Name"/> and the "labelled map layer" entry
    /// under Out of scope in <c>docs/sorting-and-map-questions.md</c>. It is why every caller shows
    /// the ISO code beside the name: a Persian player who does not read "Netherlands" still
    /// recognises NL, and the code is the same two letters in all three scripts.
    /// </para>
    /// </summary>
    public static string CountryName(IReadOnlyList<MapCountry> countries, string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";

        var match = countries.FirstOrDefault(c => string.Equals(c.Iso, iso, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match?.Name) ? iso : match.Name;
    }

    /// <summary>
    /// A point as a player reads it: <c>"52.37°, 4.9°"</c>, latitude then longitude.
    /// <para>
    /// Two decimals, which is about a kilometre — deliberately coarser than the six the wire carries.
    /// A tap on a world map is precise to tens of kilometres at best, so the extra digits are the
    /// pixel grid talking, and "52.817797°" reads as a measurement the player did not make. The
    /// answer string itself keeps every digit; only this reading of it is rounded.
    /// </para>
    /// <para>
    /// The numbers are written in the invariant culture like every other coordinate in this app —
    /// a screen is free to swap the digits for Persian ones afterwards (<c>Translator.Num</c>), but
    /// the decimal separator must stay a full stop, because this is the same text the player will
    /// compare against a target, and two different separators in one reveal would read as two
    /// different kinds of number.
    /// </para>
    /// </summary>
    public static string Coordinates(MapPick pick)
        => pick.IsPoint ? $"{Degrees(pick.Latitude!.Value)}, {Degrees(pick.Longitude!.Value)}" : "";

    private static string Degrees(double value)
        => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "°";
}

/// <summary>One column of a revealed sorting question: whose order it is, and what they placed.</summary>
/// <param name="Label">Already translated — "You", "Them", or a player's name.</param>
/// <param name="Placed">
/// The items in the order this player put them, or null if they never answered. Text rather than
/// indices, because the two paths that produce it start from different things and only agree here:
/// a reveal resolves another player's stored-index response through
/// <see cref="AnswerReveal.PlacedItems"/>, while the answering screen already has its own player's
/// order as the items the card served them. Null renders as an empty column beside the correct order
/// rather than as no column at all, so a timed-out round still shows who was being waited on.
/// </param>
public sealed record SortRevealColumn(string Label, IReadOnlyList<string>? Placed);

/// <summary>
/// One player's answer to a map question, as a reveal receives it: whose it is, the raw answer
/// string off the wire, and the verdict if one is knowable.
/// <para>
/// The string is passed through unparsed on purpose. Every reveal contract spells a map answer the
/// same way, so parsing it in one place — <c>MapReveal</c>, via <see cref="MapPick.Parse"/> — means
/// four screens cannot each decide for themselves what to do with a malformed one.
/// </para>
/// </summary>
public sealed record MapAnswerLine(string Label, string? Response, bool? Right);
