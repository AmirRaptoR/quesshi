using System.Globalization;

namespace Quesshi.Shared;

/// <summary>
/// Converts between latitude/longitude and the coordinate space of the bundled world map SVG
/// (<c>wwwroot/maps/world.svg</c>, viewBox <c>"-180 -90 360 180"</c>).
///
/// This is deliberately trivial. The map is drawn in an <b>equirectangular (Plate Carrée)</b>
/// projection specifically so that this conversion can be plain linear arithmetic rather than a
/// real map projection: longitude maps straight onto x, and latitude maps onto y with a sign
/// flip, because latitude increases northward while SVG's y axis increases downward. There is no
/// trigonometry, no ellipsoid model and no external mapping library — which is also why the
/// client (running in the browser, offline-capable as a PWA) and the server can both use this
/// exact same class and never disagree about where a tap or a stored city landed.
///
/// If the map asset ever stopped being equirectangular this class would need a real projection
/// (and every existing city question's tolerance radius would need re-checking), which is
/// precisely why <c>docs/sorting-and-map-questions.md</c> calls out asserting the projection with
/// a test rather than assuming it: most freely available world SVGs are not equirectangular, and
/// swapping one in here would make every city question wrong by hundreds of kilometres while
/// looking completely fine.
/// </summary>
public static class WorldMapProjection
{
    /// <summary>Longitude at the left edge of the viewBox.</summary>
    public const double MinLongitude = -180;

    /// <summary>Longitude at the right edge of the viewBox.</summary>
    public const double MaxLongitude = 180;

    /// <summary>Latitude at the north pole — the <em>top</em> edge of the viewBox.</summary>
    public const double MaxLatitude = 90;

    /// <summary>Latitude at the south pole — the <em>bottom</em> edge of the viewBox.</summary>
    public const double MinLatitude = -90;

    /// <summary>
    /// The SVG's <c>viewBox</c> attribute, as one source of truth shared with
    /// <c>tools/map/generate_world_map.py</c> (which produced the asset with this exact box) so a
    /// Razor component can lay markers over the map without retyping magic numbers that could
    /// silently drift out of step with the file.
    /// </summary>
    public const string ViewBox = "-180 -90 360 180";

    /// <summary>
    /// Projects a latitude/longitude pair onto the map's viewBox coordinates. Longitude passes
    /// straight through as x; latitude is negated for y because the viewBox's y axis grows
    /// downward (SVG convention) while latitude grows upward (geographic convention).
    /// </summary>
    public static (double X, double Y) Project(double lat, double lon) => (lon, -lat);

    /// <summary>The exact inverse of <see cref="Project"/>: viewBox coordinates back to lat/lon.</summary>
    public static (double Lat, double Lon) Unproject(double x, double y) => (-y, x);

    /// <summary>
    /// Mean Earth radius, the same value <c>Quesshi.Domain.Geo.EarthRadiusKm</c> uses. It is repeated
    /// rather than shared because <c>Quesshi.Shared</c> references no other project of ours — that is
    /// what keeps the wire contract free of the domain — and inverting the dependency so a browser
    /// could call into <c>Quesshi.Domain</c> would cost far more than one constant. A test pins the
    /// two together (see <c>MapAnswerFormatTests</c>), so they cannot drift in silence.
    /// </summary>
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>
    /// One degree of latitude, in kilometres. Constant everywhere on a sphere, which is exactly why
    /// the vertical half of <see cref="RadiusInDegrees"/> needs no latitude argument.
    /// </summary>
    private const double KmPerDegreeLatitude = Math.PI * EarthRadiusKm / 180;

    /// <summary>
    /// A tolerance radius in kilometres, as the viewBox radii that draw it — <b>an ellipse, not a
    /// circle</b>.
    /// <para>
    /// This is the one place the equirectangular projection stops being free. One viewBox unit is one
    /// degree in both axes, but a degree of longitude shrinks with the cosine of the latitude while a
    /// degree of latitude does not, so a circle on the globe is drawn wider than it is tall
    /// everywhere except the equator: at 60°N, 300 km east-west spans twice the degrees it spans
    /// north-south. Drawing a plain <c>&lt;circle r="km/111"&gt;</c> would show a Helsinki question's
    /// tolerance as roughly half the width it really has, which is a picture of the rules that
    /// disagrees with the rules.
    /// </para>
    /// <para>
    /// The cosine is floored so a target near a pole gives a very wide ellipse rather than a division
    /// by zero, and the horizontal radius is capped at half the world so it can never wrap past its
    /// own start.
    /// </para>
    /// </summary>
    public static (double Rx, double Ry) RadiusInDegrees(double radiusKm, double latitude)
    {
        var ry = radiusKm / KmPerDegreeLatitude;
        var cosine = Math.Max(Math.Cos(double.DegreesToRadians(latitude)), 1e-6);
        var rx = Math.Min(ry / cosine, (MaxLongitude - MinLongitude) / 2);
        return (rx, ry);
    }

    /// <summary>
    /// The wire form of a point — <c>"52.37,4.9"</c> — written the way the server writes and reads
    /// it (<c>Quesshi.Domain.Geo.Format</c>).
    /// <para>
    /// It lives here, on the class the map component already uses to turn a tap into coordinates,
    /// because the browser cannot reach <c>Geo</c>: <c>Quesshi.Web</c> references only this project.
    /// It is emphatically not a place to be clever with formatting. This app runs in Persian and a
    /// Blazor client takes the browser's culture, so a bare <c>ToString()</c> on a Persian thread
    /// emits Persian digits and an Arabic decimal separator; the server then fails to parse its own
    /// format, and the failure reads as "the map never accepts my answer" — in one language only,
    /// which is the kind of bug that ships. Hence <see cref="CultureInfo.InvariantCulture"/>, always,
    /// and a test that runs under <c>fa-IR</c> to prove it.
    /// </para>
    /// </summary>
    public static string FormatPoint(double latitude, double longitude)
        => string.Concat(FormatCoordinate(latitude), ",", FormatCoordinate(longitude));

    /// <inheritdoc cref="FormatPoint"/>
    public static string FormatCoordinate(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads back what <see cref="FormatPoint"/> wrote, and nothing else — the mirror of
    /// <c>Geo.TryParse</c>, and for the same reasons: <see cref="NumberStyles.Float"/> without
    /// <see cref="NumberStyles.AllowThousands"/> makes <c>"52,37"</c> a parse failure rather than the
    /// thousands-separated 5237 it would otherwise become, and rejects Persian digits outright.
    /// A reveal parses a target string with this, so anything malformed reads as "no target to draw"
    /// instead of a marker in the Gulf of Guinea.
    /// </summary>
    public static bool TryParsePoint(string? text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(',');
        if (parts.Length != 2) return false;

        if (!TryParseCoordinate(parts[0], out var lat) || !TryParseCoordinate(parts[1], out var lon)) return false;
        if (!double.IsFinite(lat) || lat < MinLatitude || lat > MaxLatitude) return false;
        if (!double.IsFinite(lon) || lon < MinLongitude || lon > MaxLongitude) return false;

        latitude = lat;
        longitude = lon;
        return true;
    }

    private static bool TryParseCoordinate(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
