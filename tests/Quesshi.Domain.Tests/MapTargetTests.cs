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

    // Restore mirrors Question.Restore: storage is trusted and re-validating on every load would
    // mean a bound tightened later (or a hand-edited row) throws on read and takes the whole
    // question down with it, rather than just being a stale value an admin can fix.
    [Fact]
    public void Restore_rehydrates_a_country_target_without_validating_it()
    {
        // "ZZ" is not a code City() or Country() would ever accept as a real country, but Restore
        // does not check that -- it is exactly the "bound tightened later" case this exists for.
        var target = MapTarget.Restore(MapTargetKind.Country, "ZZ", null, null, null);

        Assert.Equal(MapTargetKind.Country, target.Shape);
        Assert.True(target.IsCountry);
        Assert.Equal("ZZ", target.CountryCode);
        Assert.Null(target.Latitude);
        Assert.Null(target.Longitude);
        Assert.Null(target.RadiusKm);
    }

    [Fact]
    public void Restore_rehydrates_a_city_target_even_when_out_of_range()
    {
        // A radius of 5km is below MinRadiusKm -- City() would throw. Restore must not, because a
        // row written when the minimum was lower must still come back as the row it always was.
        var target = MapTarget.Restore(MapTargetKind.City, null, 95, 190, 5);

        Assert.Equal(MapTargetKind.City, target.Shape);
        Assert.True(target.IsCity);
        Assert.Null(target.CountryCode);
        Assert.Equal(95, target.Latitude);
        Assert.Equal(190, target.Longitude);
        Assert.Equal(5, target.RadiusKm);
    }

    /// <summary>
    /// A reveal has to name the target, and it names it in the one spelling an answer would have
    /// used — which is what makes "the answer was DE" and "you said DE" comparable at a glance
    /// rather than two formats a client has to reconcile.
    /// </summary>
    [Fact]
    public void ToResponse_writes_a_country_target_as_the_answer_that_would_have_hit_it()
    {
        var target = MapTarget.Country("de");

        Assert.Equal("DE", target.ToResponse());
        Assert.True(target.Matches(target.ToResponse()));
    }

    [Fact]
    public void ToResponse_writes_a_city_target_as_its_own_coordinates_and_they_hit_it()
    {
        var target = MapTarget.City(52.37, 4.9, 50);

        Assert.Equal(Geo.Format(52.37, 4.9), target.ToResponse());
        Assert.True(target.Matches(target.ToResponse()));
    }
}
