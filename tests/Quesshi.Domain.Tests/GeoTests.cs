using System.Globalization;
using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class GeoTests
{
    [Fact]
    public void A_point_is_written_as_lat_comma_lon()
        => Assert.Equal("52.37,4.9", Geo.Format(52.37, 4.9));

    [Fact]
    public void A_written_point_reads_back()
    {
        Assert.True(Geo.TryParse(Geo.Format(-33.8688, 151.2093), out var lat, out var lon));
        Assert.Equal(-33.8688, lat, 6);
        Assert.Equal(151.2093, lon, 6);
    }

    /// <summary>
    /// The failure this guards against is invisible in English: a Persian thread formats 52.37 as
    /// "۵۲٫۳۷", the server cannot parse its own coordinate format back, and the map appears to
    /// reject every answer in one language only.
    /// </summary>
    [Fact]
    public void Coordinates_are_ASCII_with_a_dot_even_on_a_Persian_thread()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

            var written = Geo.Format(52.37, 4.9);

            Assert.Equal("52.37,4.9", written);
            Assert.All(written, c => Assert.True(char.IsAscii(c), $"'{c}' is not ASCII."));
            Assert.Contains('.', written);

            Assert.True(Geo.TryParse(written, out var lat, out var lon));
            Assert.Equal(52.37, lat, 6);
            Assert.Equal(4.9, lon, 6);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_map_answer_is_graded_the_same_on_a_Persian_thread()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            var target = MapTarget.City(35.6892, 51.389, 50);

            Assert.True(target.Matches(Geo.Format(35.6892, 51.389)));
            Assert.False(target.Matches("۳۵.۶۸۹۲,۵۱.۳۸۹"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("52.37")]
    [InlineData("52.37,4.9,1")]
    [InlineData("here,there")]
    [InlineData("52.37;4.9")]
    // A Persian or German decimal comma, which would otherwise read as a thousands separator and
    // land the answer thousands of kilometres away rather than failing.
    [InlineData("52,37,4,9")]
    [InlineData("۵۲.۳۷,۴.۹")]
    // .NET parses these happily under the invariant culture, and every comparison against them is
    // then false — so they are rejected by name.
    [InlineData("NaN,4.9")]
    [InlineData("Infinity,4.9")]
    [InlineData("52.37,-Infinity")]
    [InlineData("90.1,0")]
    [InlineData("0,180.1")]
    public void Anything_that_is_not_our_own_format_is_refused(string? text)
        => Assert.False(Geo.TryParse(text, out _, out _));

    [Fact]
    public void Surrounding_space_is_tolerated()
    {
        Assert.True(Geo.TryParse(" 52.37 , 4.9 ", out var lat, out var lon));
        Assert.Equal(52.37, lat, 6);
        Assert.Equal(4.9, lon, 6);
    }

    [Fact]
    public void The_distance_from_a_point_to_itself_is_zero()
        => Assert.Equal(0, Geo.DistanceKm(52.37, 4.9, 52.37, 4.9), 9);

    [Fact]
    public void Amsterdam_to_Paris_is_about_430_km()
        => Assert.Equal(430, Geo.DistanceKm(52.37, 4.9, 48.8566, 2.3522), 0);

    [Fact]
    public void Half_the_way_round_the_equator_is_half_the_circumference()
        => Assert.Equal(20015, Geo.DistanceKm(0, 0, 0, 180), 0);

    [Fact]
    public void A_non_finite_coordinate_is_never_valid()
    {
        Assert.False(Geo.IsValidLatitude(double.NaN));
        Assert.False(Geo.IsValidLatitude(double.PositiveInfinity));
        Assert.False(Geo.IsValidLongitude(double.NaN));
        Assert.False(Geo.IsValidLongitude(double.NegativeInfinity));
        Assert.True(Geo.IsValidLatitude(-90));
        Assert.True(Geo.IsValidLongitude(180));
    }
}
