using System.Globalization;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// What a tap on the map turns into, and what a reveal turns a stored answer back into. The pair
/// have to agree exactly with each other and with the server's <c>MapTarget.ToResponse</c>, because
/// the string that goes up is the same string that comes back down at reveal — a player's own answer
/// is shown to them by parsing what they sent.
/// </summary>
public class MapPickTests
{
    [Fact]
    public void A_country_pick_submits_its_code()
    {
        var pick = MapPick.Country("de");

        Assert.True(pick.IsCountry);
        Assert.False(pick.IsPoint);

        // Upper-cased on the way in, so a tap, a stored target and the map's own data-iso attribute
        // are all the same two characters when they meet.
        Assert.Equal("DE", pick.Response);
    }

    [Fact]
    public void A_point_pick_submits_lat_then_lon()
    {
        var pick = MapPick.Point(52.37, 4.9);

        Assert.True(pick.IsPoint);
        Assert.False(pick.IsCountry);
        Assert.Equal("52.37,4.9", pick.Response);
    }

    /// <summary>
    /// The failure this guards against is invisible in English: a Persian thread formats 52.37 as
    /// "۵۲٫۳۷", the server refuses to parse it, and the map appears to reject every answer — in one
    /// language only, which is the kind of bug that ships. The server's own <c>GeoTests</c> pins the
    /// same rule from the other side.
    /// </summary>
    [Fact]
    public void A_point_is_ASCII_with_a_dot_even_on_a_Persian_thread()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

            var response = MapPick.Point(35.6892, 51.389).Response;

            Assert.Equal("35.6892,51.389", response);
            Assert.All(response, c => Assert.True(char.IsAscii(c), $"'{c}' is not ASCII."));
            Assert.Contains('.', response);
            Assert.DoesNotContain('٫', response);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_pick_survives_the_round_trip_through_its_own_response()
    {
        // This is the reconnect path: a live client that dropped mid-round gets its own map answer
        // back as a string and has to put the pin where the player left it.
        var point = MapPick.Parse(MapPick.Point(-33.8688, 151.2093).Response);

        Assert.NotNull(point);
        Assert.Equal(-33.8688, point.Latitude!.Value, 6);
        Assert.Equal(151.2093, point.Longitude!.Value, 6);

        var country = MapPick.Parse(MapPick.Country("NL").Response);

        Assert.NotNull(country);
        Assert.Equal("NL", country.CountryCode);
    }

    [Theory]
    [InlineData("DE", "DE")]
    [InlineData("nl", "NL")]
    public void A_two_letter_target_reads_as_a_country(string stored, string expected)
    {
        var pick = MapPick.Parse(stored);

        Assert.NotNull(pick);
        Assert.True(pick.IsCountry);
        Assert.Equal(expected, pick.CountryCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Berlin")]
    [InlineData("52.37")]
    [InlineData("52.37,4.9,3")]
    [InlineData("۵۲.۳۷,۴.۹")]   // Persian digits: rejected, never guessed at
    [InlineData("91,4.9")]      // off the globe
    [InlineData("52.37,181")]
    [InlineData("NaN,4.9")]
    public void An_answer_that_is_not_a_place_is_no_place_at_all(string? stored)
    {
        // Null rather than an exception, and null rather than a plausible-looking fallback: every
        // caller of Parse is a reveal rendering a string that arrived over the wire, and a reveal
        // that cannot read a target must draw no marker rather than one in the Gulf of Guinea.
        Assert.Null(MapPick.Parse(stored));
    }
}

/// <summary>
/// The wire form of a point, and the geometry of the tolerance a city answer is judged against.
/// These live on <see cref="WorldMapProjection"/> rather than in the browser project because the
/// map component is the only thing that produces them and the reveal is the only thing that reads
/// them back — and because <c>Quesshi.Web</c> cannot reach the server's <c>Geo</c>.
/// </summary>
public class MapAnswerFormatTests
{
    [Fact]
    public void A_written_point_reads_back_unchanged()
    {
        Assert.True(WorldMapProjection.TryParsePoint(WorldMapProjection.FormatPoint(52.3676, 4.9041),
            out var lat, out var lon));

        Assert.Equal(52.3676, lat, 6);
        Assert.Equal(4.9041, lon, 6);
    }

    [Fact]
    public void Persian_digits_are_refused_rather_than_read_as_something_else()
    {
        // A Blazor client takes the browser's culture, so this is what a bare ToString() would have
        // produced for most of this app's players. Refusing it outright is the point: a coordinate
        // that half-parses is worse than one that does not parse, because the failure then arrives
        // as a pin in the wrong ocean instead of as nothing at all.
        Assert.False(WorldMapProjection.TryParsePoint("۵۲.۳۷,۴.۹", out _, out _));
        Assert.False(WorldMapProjection.TryParsePoint("52٫37,4٫9", out _, out _));
    }

    [Fact]
    public void The_tolerance_is_taller_than_it_is_wide_only_at_the_equator()
    {
        // One viewBox unit is one degree in both axes, but a degree of longitude shrinks with the
        // cosine of the latitude and a degree of latitude does not. A plain circle here would draw
        // a northern city's tolerance at roughly half its real width.
        var (rxEquator, ryEquator) = WorldMapProjection.RadiusInDegrees(300, latitude: 0);
        Assert.Equal(rxEquator, ryEquator, 6);

        var (rx60, ry60) = WorldMapProjection.RadiusInDegrees(300, latitude: 60);
        Assert.Equal(ry60, ryEquator, 6);           // the vertical radius never changes
        Assert.Equal(2 * ry60, rx60, precision: 2); // cos 60 = 0.5, so twice as wide
    }

    [Fact]
    public void A_radius_in_kilometres_becomes_the_right_number_of_degrees()
    {
        // One degree of latitude is about 111.19 km on this sphere, so the smallest radius a
        // question may carry (10 km) is about a tenth of a degree and the largest (2000 km) about 18.
        var (_, small) = WorldMapProjection.RadiusInDegrees(10, latitude: 0);
        var (_, large) = WorldMapProjection.RadiusInDegrees(2000, latitude: 0);

        Assert.Equal(0.0899, small, 3);
        Assert.Equal(17.986, large, 3);
    }

    [Fact]
    public void A_target_at_a_pole_gives_a_wide_ellipse_rather_than_a_division_by_zero()
    {
        var (rx, ry) = WorldMapProjection.RadiusInDegrees(300, latitude: 90);

        Assert.True(double.IsFinite(rx));
        Assert.True(double.IsFinite(ry));

        // And never wider than half the world, or the ellipse would wrap past its own start.
        Assert.Equal(180, rx);
    }
}
