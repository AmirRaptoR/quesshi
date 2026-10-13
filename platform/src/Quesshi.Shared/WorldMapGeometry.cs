using System.Globalization;
using System.Text.RegularExpressions;

namespace Quesshi.Shared;

/// <summary>
/// Whether a point on the globe is inside a country, answered against the outlines of the very map
/// the game draws (<c>wwwroot/maps/world.svg</c>, embedded here by <see cref="WorldMapSvg"/>).
///
/// <para>
/// This exists for one job in the generation pipeline. A model asked for a city question will
/// confidently return a plausible city, a plausible country and coordinates that are off by a whole
/// country — "Porto, PT, 41.15, -8.61" is right, and the same model will as happily produce
/// "Porto, PT, 41.90, 12.50", which is Rome. Nothing about that is detectable from the strings; the
/// only thing that can catch it is the map. So the pipeline asks for the city <i>and</i> its
/// country, and this decides whether the two agree before the question is stored.
/// </para>
///
/// <para>
/// <b>Why here and not by re-parsing the asset a third time.</b> <c>Quesshi.Web</c>'s
/// <c>WorldMapAsset</c> already parses the same file in the browser, for rendering — it fetches it
/// over HTTP and keeps the path data as opaque strings to hand back to the SVG element, which is all
/// a renderer needs. The server needs the outlines as numbers, and it needs them without a browser,
/// so the parse lives beside <see cref="WorldMapCountries"/> in the project that already embeds the
/// file. Both read the same bytes: edit the map and the codes, the drawing and this check all move
/// together, which is the property the whole "embed the asset" arrangement exists to buy.
/// </para>
///
/// <para>
/// <b>The arithmetic is only this simple because the projection is equirectangular.</b> The viewBox
/// is <c>"-180 -90 360 180"</c> with x = longitude and y = −latitude
/// (<see cref="WorldMapProjection"/>), so a path's coordinates <i>are</i> degrees and a
/// point-in-polygon test in viewBox space <i>is</i> a point-in-country test on the globe. Against a
/// Robinson or Mercator asset this class would be silently, catastrophically wrong — which is why
/// the projection is pinned by its own test rather than assumed.
/// </para>
/// </summary>
public static partial class WorldMapGeometry
{
    /// <summary>
    /// One degree of latitude in kilometres — the same figure <c>WorldMapProjection</c> works from,
    /// used here only to turn a slack expressed in kilometres into the degrees the outlines are
    /// drawn in.
    /// </summary>
    private const double KmPerDegree = Math.PI * 6371.0088 / 180;

    private static readonly Lazy<IReadOnlyDictionary<string, double[][]>> LazyRings = new(Load);

    /// <summary>
    /// Every code this could parse a usable outline for. It should be — and a test insists it is —
    /// exactly <see cref="WorldMapCountries.Codes"/>: a country the map draws but whose outline did
    /// not survive the parse would silently reject every true city question written about it, and
    /// the only symptom would be one bucket that never fills.
    /// </summary>
    public static IReadOnlySet<string> Outlined => LazyRings.Value.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the point falls inside the country the map draws under <paramref name="countryCode"/>.
    ///
    /// <para>
    /// <paramref name="toleranceKm"/> is slack around the outline, and it is not a nicety. These
    /// borders come from Natural Earth 1:110m, rounded to two decimal places for file size — a
    /// deliberately coarse rendering asset, not a cadastral boundary. A perfectly correct coastal
    /// city (Lisbon on its estuary, Copenhagen on its strait, anything on a delta or a narrow
    /// peninsula) can therefore sit a handful of kilometres <i>outside</i> its own country's
    /// simplified outline. Rejecting those would throw away true questions in the name of a
    /// data-file artefact. The error this check exists to catch is off by hundreds of kilometres, so
    /// a slack far larger than the asset's own error and far smaller than a country keeps it doing
    /// its job.
    /// </para>
    ///
    /// <para>
    /// False for a code the map does not draw at all, and for a non-finite coordinate: an unknown
    /// country is not somewhere a question can be authored against, and NaN is inside nothing —
    /// stating that here as well means a caller cannot get a "yes" out of this by passing rubbish.
    /// </para>
    /// </summary>
    public static bool Contains(string? countryCode, double latitude, double longitude, double toleranceKm = 0)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return false;
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)) return false;

        if (!LazyRings.Value.TryGetValue(countryCode.Trim().ToUpperInvariant(), out var rings)) return false;

        var (x, y) = WorldMapProjection.Project(latitude, longitude);

        if (IsInside(rings, x, y)) return true;
        if (toleranceKm <= 0) return false;

        return DistanceToOutlineKm(rings, x, y, latitude) <= toleranceKm;
    }

    /// <summary>
    /// The even-odd rule — the same <c>fill-rule="evenodd"</c> the map itself is drawn with, so
    /// "inside" here means exactly the pixels a player sees filled. It also gets holes right for
    /// free: an enclave drawn as an inner ring crosses the ray one extra time and lands outside,
    /// which is what the eye sees too.
    /// </summary>
    private static bool IsInside(double[][] rings, double x, double y)
    {
        var inside = false;

        foreach (var ring in rings)
        {
            // Each ring is a flat [x0,y0,x1,y1,...] array; pairing them up in place keeps ~10k
            // points from becoming ~10k little objects for a check that runs on every candidate.
            for (int i = 0, j = ring.Length - 2; i < ring.Length; j = i, i += 2)
            {
                double xi = ring[i], yi = ring[i + 1], xj = ring[j], yj = ring[j + 1];

                // A horizontal ray cast east from the point. The half-open comparison on y is what
                // stops a vertex exactly level with the ray being counted twice.
                if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// How far outside the outline the point is, in kilometres — the shortest distance to any edge.
    /// <para>
    /// Distance is measured with longitude scaled by the cosine of the latitude, because a degree of
    /// longitude is not a degree of distance anywhere but the equator: at 60°N it is half a degree's
    /// worth. Skipping that scaling would make the slack twice as generous in Scandinavia as at the
    /// tropics, which is precisely the kind of quiet inconsistency that makes a rejection reason
    /// impossible to reason about later.
    /// </para>
    /// </summary>
    private static double DistanceToOutlineKm(double[][] rings, double x, double y, double latitude)
    {
        var scale = Math.Max(Math.Cos(double.DegreesToRadians(latitude)), 1e-6);
        var best = double.MaxValue;

        foreach (var ring in rings)
        {
            for (int i = 0, j = ring.Length - 2; i < ring.Length; j = i, i += 2)
            {
                var distance = DistanceToSegment((x - ring[j]) * scale, y - ring[j + 1],
                    (ring[i] - ring[j]) * scale, ring[i + 1] - ring[j + 1]);

                if (distance < best) best = distance;
            }
        }

        return best * KmPerDegree;
    }

    /// <summary>
    /// Distance from the origin to the segment that starts at <c>-(px, py)</c> and runs along
    /// <c>(vx, vy)</c> — i.e. the caller has already moved the point to the origin, which saves
    /// repeating the subtraction for every one of ten thousand edges.
    /// </summary>
    private static double DistanceToSegment(double px, double py, double vx, double vy)
    {
        var lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= 0) return Math.Sqrt(px * px + py * py);

        // Where the foot of the perpendicular falls along the segment, clamped to its ends so a
        // point beyond either end measures to that end rather than to the infinite line.
        var t = Math.Clamp((px * vx + py * vy) / lengthSquared, 0, 1);
        var dx = px - t * vx;
        var dy = py - t * vy;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Pulls every country's outlines out of the asset. The generator
    /// (<c>tools/map/generate_world_map.py</c>) emits nothing but absolute <c>M</c>, <c>L</c> and
    /// <c>Z</c> — one ring is <c>M x,y L x,y … Z</c> — so this reads that and only that. A path
    /// using curves or relative commands would be a different file than the one this project
    /// produces, and guessing at it would be worse than not loading it: an outline half-parsed into
    /// nonsense would reject true questions and accept false ones with equal confidence, so an
    /// unparseable ring is dropped rather than approximated.
    /// </summary>
    private static IReadOnlyDictionary<string, double[][]> Load()
    {
        var countries = new Dictionary<string, double[][]>(StringComparer.Ordinal);

        foreach (Match match in CountryPath().Matches(WorldMapSvg.Text))
        {
            var rings = ParseRings(match.Groups[2].Value);
            if (rings.Length > 0) countries[match.Groups[1].Value] = rings;
        }

        if (countries.Count == 0)
            throw new InvalidOperationException(
                "world.svg was embedded but no country outlines could be parsed out of it — is it the right file?");

        return countries;
    }

    /// <summary>Splits one path's <c>d</c> into its rings, each a flat array of viewBox coordinates.</summary>
    private static double[][] ParseRings(string d)
    {
        var rings = new List<double[]>();

        foreach (var subpath in d.Split('Z', StringSplitOptions.RemoveEmptyEntries))
        {
            var points = new List<double>();

            foreach (var pair in subpath.Split(['M', 'L'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(',');
                if (parts.Length != 2) { points.Clear(); break; }

                // Invariant culture, as everywhere a coordinate is read: the file is written with a
                // dot and this code may well be running on a Persian thread.
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    points.Clear();
                    break;
                }

                points.Add(x);
                points.Add(y);
            }

            // Fewer than three points is a degenerate ring with no inside at all.
            if (points.Count >= 6) rings.Add([.. points]);
        }

        return [.. rings];
    }

    [GeneratedRegex("<path id=\"([A-Z]{2})\"[^>]*\\sd=\"([^\"]+)\"")]
    private static partial Regex CountryPath();
}
