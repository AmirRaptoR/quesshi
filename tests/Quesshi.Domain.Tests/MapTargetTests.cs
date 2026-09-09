using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MapTargetTests
{
    [Fact]
    public void A_country_code_is_trimmed_and_upper_cased()
    {
        var target = MapTarget.Country("  de ");

        Assert.Equal(MapTargetKind.Country, target.Shape);
        Assert.True(target.IsCountry);
        Assert.False(target.IsCity);
        Assert.Equal("DE", target.CountryCode);
        Assert.Null(target.Latitude);
        Assert.Null(target.Longitude);
        Assert.Null(target.RadiusKm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]
    [InlineData("DEU")]
    [InlineData("D1")]
    [InlineData("۳۴")]
    public void A_code_that_is_not_alpha_2_is_refused(string code)
        => Assert.Throws<ArgumentException>(() => MapTarget.Country(code));

    [Fact]
    public void A_city_carries_a_point_and_a_radius()
    {
        var target = MapTarget.City(52.37, 4.9, 50);

        Assert.Equal(MapTargetKind.City, target.Shape);
        Assert.True(target.IsCity);
        Assert.Null(target.CountryCode);
        Assert.Equal(52.37, target.Latitude);
        Assert.Equal(4.9, target.Longitude);
        Assert.Equal(50, target.RadiusKm);
    }

    [Theory]
    [InlineData(90.0001)]
    [InlineData(-90.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_impossible_latitude_is_refused(double latitude)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MapTarget.City(latitude, 0, 50));

    [Theory]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_impossible_longitude_is_refused(double longitude)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MapTarget.City(0, longitude, 50));

    [Theory]
    [InlineData(9.99)]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(2000.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_radius_outside_10_to_2000_km_is_refused(double radiusKm)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MapTarget.City(52.37, 4.9, radiusKm));

    [Fact]
    public void The_ends_of_every_range_are_allowed()
    {
        Assert.Equal(90, MapTarget.City(90, 180, MapTarget.MinRadiusKm).Latitude);
        Assert.Equal(-180, MapTarget.City(-90, -180, MapTarget.MaxRadiusKm).Longitude);
    }

    [Fact]
    public void Two_targets_with_the_same_values_are_the_same_target()
        => Assert.Equal(MapTarget.City(52.37, 4.9, 50), MapTarget.City(52.37, 4.9, 50));
}
