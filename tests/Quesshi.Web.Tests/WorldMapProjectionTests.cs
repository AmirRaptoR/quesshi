using System.Globalization;
using System.Text.RegularExpressions;
using Quesshi.Shared;

namespace Quesshi.Web.Tests;

/// <summary>
/// The whole point of this asset is that it is equirectangular (Plate Carrée), and that has to be
/// asserted against the real, shipped file rather than assumed or checked against a fixture —
/// see <c>docs/sorting-and-map-questions.md</c>. Most freely available world SVGs are Robinson,
/// Miller or Mercator, which would make every city question silently wrong by hundreds of
/// kilometres while looking perfectly reasonable, so these tests read
/// <c>wwwroot/maps/world.svg</c> exactly as it ships (copied into the test output by
/// <c>Quesshi.Web.Tests.csproj</c>, the same way <see cref="BrowserTranslationTests"/> reads the
/// real i18n files) and check landmarks that only hold for that specific projection.
/// </summary>
public class WorldMapProjectionTests
{
    private static readonly string SvgPath = Path.Combine(AppContext.BaseDirectory, "maps", "world.svg");
    private static readonly string Svg = File.ReadAllText(SvgPath);

    private static (double MinX, double MinY, double Width, double Height) ReadViewBox()
    {
        var match = Regex.Match(Svg, @"viewBox=""([^""]+)""");
        Assert.True(match.Success, "world.svg has no viewBox attribute.");

        var parts = match.Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(4, parts.Length);
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>
    /// Pulls the raw path data for one country's ISO code straight out of the real file, so the
    /// bounding-box test below exercises the asset exactly as generated, not a re-derived model
    /// of it.
    /// </summary>
    private static (double MinX, double MaxX, double MinY, double MaxY) BoundingBoxOf(string iso)
    {
        var pathMatch = Regex.Match(Svg, $@"<path id=""{iso}""[^>]*\sd=""([^""]+)""");
        Assert.True(pathMatch.Success, $"No <path id=\"{iso}\"> found in world.svg.");

        var numbers = Regex.Matches(pathMatch.Groups[1].Value, @"-?\d+(\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.True(numbers.Length >= 2 && numbers.Length % 2 == 0,
            $"Expected an even, non-zero count of coordinate numbers for {iso}, got {numbers.Length}.");

        var xs = numbers.Where((_, i) => i % 2 == 0).ToArray();
        var ys = numbers.Where((_, i) => i % 2 == 1).ToArray();
        return (xs.Min(), xs.Max(), ys.Min(), ys.Max());
    }

    [Fact]
    public void ViewBox_matches_the_shared_projection_constant()
    {
        // If someone regenerates the asset with a different box, the shared projection helper
        // and the file it is supposed to describe would quietly disagree. Pin them together.
        var (minX, minY, width, height) = ReadViewBox();
        var box = $"{minX:0} {minY:0} {width:0} {height:0}";
        Assert.Equal(WorldMapProjection.ViewBox, box);
    }

    [Fact]
    public void Equator_and_prime_meridian_project_to_the_viewBox_centre()
    {
        var (minX, minY, width, height) = ReadViewBox();
        var centre = (X: minX + width / 2, Y: minY + height / 2);

        var (x, y) = WorldMapProjection.Project(lat: 0, lon: 0);

        Assert.Equal(centre.X, x, precision: 9);
        Assert.Equal(centre.Y, y, precision: 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(-30)]
    [InlineData(90)]
    [InlineData(-90)]
    public void Longitude_zero_lands_on_the_vertical_midline_at_any_latitude(double lat)
    {
        var (minX, _, width, _) = ReadViewBox();
        var midline = minX + width / 2;

        var (x, _) = WorldMapProjection.Project(lat, lon: 0);

        Assert.Equal(midline, x, precision: 9);
    }

    [Theory]
    [InlineData(-180)]
    [InlineData(0)]
    [InlineData(179.99)]
    public void Latitude_90_lands_on_the_top_edge_at_any_longitude(double lon)
    {
        var (_, minY, _, _) = ReadViewBox();

        var (_, y) = WorldMapProjection.Project(lat: 90, lon);

        Assert.Equal(minY, y, precision: 9);
    }

    [Theory]
    [InlineData(-180)]
    [InlineData(0)]
    [InlineData(179.99)]
    public void Latitude_negative_90_lands_on_the_bottom_edge_at_any_longitude(double lon)
    {
        var (_, minY, _, height) = ReadViewBox();
        var bottom = minY + height;

        var (_, y) = WorldMapProjection.Project(lat: -90, lon);

        Assert.Equal(bottom, y, precision: 9);
    }

    [Fact]
    public void Tehran_falls_inside_IRs_bounding_box()
    {
        // Tehran, Iran: 35.69 N, 51.39 E. If the asset were Mercator, Robinson or Miller instead
        // of equirectangular, or if Project used the wrong sign, this is the kind of check that
        // would fail loudly instead of quietly serving a wrong answer as correct.
        var (x, y) = WorldMapProjection.Project(lat: 35.69, lon: 51.39);
        var (minX, maxX, minY, maxY) = BoundingBoxOf("IR");

        Assert.InRange(x, minX, maxX);
        Assert.InRange(y, minY, maxY);
    }

    [Fact]
    public void Tehran_falls_inside_the_IR_polygon()
    {
        // The acceptance bar only asks for the bounding-box check above; this is the bonus full
        // point-in-polygon test using the SVG's own fill-rule (evenodd), which is also what
        // correctly handles a country drawn as several disjoint rings (islands) or with a hole.
        var pathMatch = Regex.Match(Svg, @"<path id=""IR""[^>]*\sd=""([^""]+)""");
        Assert.True(pathMatch.Success);

        var rings = ParseRings(pathMatch.Groups[1].Value);
        var (x, y) = WorldMapProjection.Project(lat: 35.69, lon: 51.39);

        Assert.True(ContainsEvenOdd(rings, x, y), $"({x},{y}) is not inside IR's polygon.");
    }

    /// <summary>Splits a "MxLxLx...ZMxLx...Z" path (exactly the shape the generator emits) into rings.</summary>
    private static List<(double X, double Y)[]> ParseRings(string d)
        => Regex.Matches(d, @"M([^Z]*)Z")
            .Select(m => m.Groups[1].Value
                .Split('L', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair =>
                {
                    var xy = pair.Split(',');
                    return (double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture));
                })
                .ToArray())
            .ToList();

    /// <summary>
    /// Standard ray-casting point-in-polygon test, run once across every ring's edges. Summing
    /// crossings across rings this way reproduces SVG's <c>fill-rule="evenodd"</c> exactly: a
    /// point inside an odd number of rings (a filled area, or a filled area minus a hole minus
    /// another hole...) is inside, and an even number is outside.
    /// </summary>
    private static bool ContainsEvenOdd(List<(double X, double Y)[]> rings, double px, double py)
    {
        var inside = false;
        foreach (var ring in rings)
        {
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var (xi, yi) = ring[i];
                var (xj, yj) = ring[j];
                var crosses = yi > py != yj > py && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
                if (crosses) inside = !inside;
            }
        }
        return inside;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, -180)]
    [InlineData(-90, 180)]
    [InlineData(35.6892, 51.3890)]
    [InlineData(52.3676, 4.9041)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(40.7128, -74.006)]
    public void Round_trip_returns_the_original_lat_lon(double lat, double lon)
    {
        var (x, y) = WorldMapProjection.Project(lat, lon);
        var (roundTrippedLat, roundTrippedLon) = WorldMapProjection.Unproject(x, y);

        Assert.Equal(lat, roundTrippedLat, precision: 9);
        Assert.Equal(lon, roundTrippedLon, precision: 9);
    }
}
