using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// One place on the world map: a country, or a point. This is what a tap produces, what a reveal
/// reads back out of a target or a response string, and what the map component draws a highlight or
/// a pin for — one type for all three so the play screen, the reveal and #77's admin form cannot
/// each invent their own spelling of "where".
/// <para>
/// Exactly one shape is populated, which is the same rule <c>Quesshi.Domain.MapTarget</c> holds to
/// on the server, and for the same reason: a pick that carried both a country code and a pair of
/// coordinates would leave every reader deciding for itself which one to believe. The private
/// constructor plus the two named factories is what makes that true by construction rather than by
/// a rule someone has to remember.
/// </para>
/// </summary>
public sealed record MapPick
{
    private MapPick(string? countryCode, double? latitude, double? longitude)
    {
        CountryCode = countryCode;
        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>Upper-case ISO 3166-1 alpha-2, e.g. <c>"DE"</c>. Null for a point.</summary>
    public string? CountryCode { get; }

    /// <summary>Degrees north. Null for a country.</summary>
    public double? Latitude { get; }

    /// <summary>Degrees east. Null for a country.</summary>
    public double? Longitude { get; }

    public bool IsCountry => CountryCode is not null;
    public bool IsPoint => Latitude is not null;

    /// <summary>
    /// A country pick. The code is upper-cased here so a tap, a reveal's target string and the
    /// <c>data-iso</c> attribute on the SVG path all compare as the same thing — the map's own
    /// attributes are upper-case, and a stored target may not be.
    /// </summary>
    public static MapPick Country(string countryCode) => new(countryCode.Trim().ToUpperInvariant(), null, null);

    /// <summary>A dropped pin. Coordinates are already in degrees; converting a tap into them is
    /// <see cref="WorldMapProjection.Unproject"/>'s job, one layer up.</summary>
    public static MapPick Point(double latitude, double longitude) => new(null, latitude, longitude);

    /// <summary>
    /// Reads an answer or target string back into a pick: a two-letter code is a country, anything
    /// else is tried as <c>"lat,lon"</c>, and anything that is neither returns null.
    /// <para>
    /// Null rather than an exception, because every caller of this is a <b>reveal</b> — a screen
    /// rendering a string that arrived over the wire, possibly written by an older build. A reveal
    /// that cannot parse a target must draw no marker; it must not take the results screen down with
    /// it, and it must certainly not put a marker somewhere plausible-looking instead.
    /// </para>
    /// </summary>
    public static MapPick? Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var text = response.Trim();

        // A country code and a coordinate pair cannot be confused for one another: the one has two
        // letters and no comma, the other has a comma and no letters. Testing the letters first
        // means a malformed pair never gets read as a country.
        if (text.Length == 2 && text.All(char.IsAsciiLetter)) return Country(text);

        return WorldMapProjection.TryParsePoint(text, out var latitude, out var longitude)
            ? Point(latitude, longitude)
            : null;
    }

    /// <summary>
    /// This pick as the string <c>AnswerDto.Response</c> carries — a country code, or
    /// <c>"52.37,4.9"</c> in the invariant culture. It is deliberately the exact same form the
    /// server's <c>MapTarget.ToResponse</c> writes, so what a player submits and what a reveal shows
    /// them as the answer are the same kind of thing, spelled the same way.
    /// </summary>
    public string Response => IsCountry
        ? CountryCode!
        : WorldMapProjection.FormatPoint(Latitude!.Value, Longitude!.Value);
}

/// <summary>
/// One thing drawn on a revealed map: somebody's answer, whose it was, and whether it was right.
/// </summary>
/// <param name="Pick">Where they said.</param>
/// <param name="Label">Whose answer it is, already translated — "You", "Them", or a player's name.</param>
/// <param name="Right">
/// True right, false wrong, and <b>null when this side cannot tell</b>. Null is a real case, not a
/// placeholder: a city target is judged against a tolerance radius that no reveal contract carries,
/// so an async duel's history can show where the answer was and where the pin went without being
/// able to state the verdict. Drawing a confident red for "probably missed" would be worse than
/// drawing neither colour.
/// </param>
public sealed record MapMark(MapPick Pick, string Label, bool? Right);
