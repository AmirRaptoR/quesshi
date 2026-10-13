using System.Globalization;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class GeoTests
{
    [Fact]
    public void A_point_is_written_as_lat_comma_lon()
        => Assert.Equal("52.37,4.9", Geo.Format(52.37, 4.9));

    [Fact]
    public void A_written_point_reads_back()
    {
        Assert.True(Geo.TryParse(Geo.Format(-33.8688, 151.2093), out var latitude, out var longitude));
        Assert.Equal(-33.8688, latitude, 6);
        Assert.Equal(151.2093, longitude, 6);
    }

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
            Assert.True(Geo.TryParse(written, out var latitude, out var longitude));
            Assert.Equal(52.37, latitude, 6);
            Assert.Equal(4.9, longitude, 6);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("52.37")]
    [InlineData("52.37,4.9,1")]
    [InlineData("here,there")]
    [InlineData("52.37;4.9")]
    [InlineData("52,37,4,9")]
    [InlineData("۵۲.۳۷,۴.۹")]
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
        Assert.True(Geo.TryParse(" 52.37 , 4.9 ", out var latitude, out var longitude));
        Assert.Equal(52.37, latitude, 6);
        Assert.Equal(4.9, longitude, 6);
    }

    [Fact]
    public void Great_circle_distance_handles_local_and_global_distances()
    {
        Assert.Equal(0, Geo.DistanceKm(52.37, 4.9, 52.37, 4.9), 9);
        Assert.Equal(430, Geo.DistanceKm(52.37, 4.9, 48.8566, 2.3522), 0);
        Assert.Equal(20015, Geo.DistanceKm(0, 0, 0, 180), 0);
    }

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
