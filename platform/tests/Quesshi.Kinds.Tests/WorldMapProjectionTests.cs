using System.Globalization;
using System.Text.RegularExpressions;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class WorldMapProjectionTests
{
    private static readonly string Svg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "world.svg"));

    [Fact]
    public void ViewBox_matches_the_shared_projection_constant()
    {
        var (minX, minY, width, height) = ReadViewBox();
        Assert.Equal(WorldMapProjection.ViewBox, $"{minX:0} {minY:0} {width:0} {height:0}");
    }

    [Fact]
    public void Equator_and_prime_meridian_project_to_the_viewBox_centre()
    {
        var (minX, minY, width, height) = ReadViewBox();
        var (x, y) = WorldMapProjection.Project(0, 0);
        Assert.Equal(minX + width / 2, x, 9);
        Assert.Equal(minY + height / 2, y, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(-30)]
    [InlineData(90)]
    [InlineData(-90)]
    public void Longitude_zero_lands_on_the_vertical_midline_at_any_latitude(double latitude)
        => Assert.Equal(0, WorldMapProjection.Project(latitude, 0).X, 9);

    [Theory]
    [InlineData(-180)]
    [InlineData(0)]
    [InlineData(179.99)]
    public void Poles_project_to_the_viewBox_edges(double longitude)
    {
        Assert.Equal(-90, WorldMapProjection.Project(90, longitude).Y, 9);
        Assert.Equal(90, WorldMapProjection.Project(-90, longitude).Y, 9);
    }

    [Fact]
    public void Tehran_falls_inside_IRs_bounding_box_and_polygon()
    {
        var (x, y) = WorldMapProjection.Project(35.69, 51.39);
        var (minX, maxX, minY, maxY) = BoundingBoxOf("IR");
        Assert.InRange(x, minX, maxX);
        Assert.InRange(y, minY, maxY);
        Assert.True(WorldMapGeometry.Contains("IR", 35.69, 51.39));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, -180)]
    [InlineData(-90, 180)]
    [InlineData(35.6892, 51.3890)]
    [InlineData(52.3676, 4.9041)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(40.7128, -74.006)]
    public void Round_trip_returns_the_original_latitude_and_longitude(double latitude, double longitude)
    {
        var (x, y) = WorldMapProjection.Project(latitude, longitude);
        var (roundTrippedLatitude, roundTrippedLongitude) = WorldMapProjection.Unproject(x, y);
        Assert.Equal(latitude, roundTrippedLatitude, 9);
        Assert.Equal(longitude, roundTrippedLongitude, 9);
    }

    [Fact]
    public void Point_formatting_and_parsing_are_culture_invariant()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("52.37,4.9", WorldMapProjection.FormatPoint(52.37, 4.9));
            Assert.True(WorldMapProjection.TryParsePoint("52.37,4.9", out var latitude, out var longitude));
            Assert.Equal(52.37, latitude, 6);
            Assert.Equal(4.9, longitude, 6);
            Assert.False(WorldMapProjection.TryParsePoint("52,37,4,9", out _, out _));
            Assert.False(WorldMapProjection.TryParsePoint("NaN,4.9", out _, out _));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(0, 181)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NaN)]
    public void Point_parser_refuses_out_of_range_and_non_finite_values(double latitude, double longitude)
        => Assert.False(WorldMapProjection.TryParsePoint($"{latitude.ToString(CultureInfo.InvariantCulture)},{longitude.ToString(CultureInfo.InvariantCulture)}", out _, out _));

    [Fact]
    public void Radius_in_degrees_is_an_ellipse_and_is_capped_near_the_poles()
    {
        var equator = WorldMapProjection.RadiusInDegrees(300, 0);
        var highLatitude = WorldMapProjection.RadiusInDegrees(300, 60);
        var pole = WorldMapProjection.RadiusInDegrees(300, 90);
        Assert.Equal(equator.Rx, equator.Ry, 9);
        Assert.Equal(equator.Ry * 2, highLatitude.Rx, 9);
        Assert.Equal(equator.Ry, highLatitude.Ry, 9);
        Assert.Equal(180, pole.Rx, 9);
    }

    private static (double MinX, double MinY, double Width, double Height) ReadViewBox()
    {
        var match = Regex.Match(Svg, "viewBox=\"([^\"]+)\"");
        Assert.True(match.Success, "world.svg has no viewBox attribute.");
        var parts = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, parts.Length);
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) BoundingBoxOf(string iso)
    {
        var path = Regex.Match(Svg, $"<path id=\"{iso}\"[^>]*\\sd=\"([^\"]+)\"");
        Assert.True(path.Success, $"No path for {iso} found in world.svg.");
        var numbers = Regex.Matches(path.Groups[1].Value, "-?\\d+(\\.\\d+)?")
            .Select(number => double.Parse(number.Value, CultureInfo.InvariantCulture)).ToArray();
        var xs = numbers.Where((_, index) => index % 2 == 0).ToArray();
        var ys = numbers.Where((_, index) => index % 2 == 1).ToArray();
        return (xs.Min(), xs.Max(), ys.Min(), ys.Max());
    }
}
