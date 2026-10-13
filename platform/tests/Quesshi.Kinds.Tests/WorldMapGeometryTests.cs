using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class WorldMapGeometryTests
{
    private const double AmsterdamLat = 52.37, AmsterdamLon = 4.90;
    private const double BerlinLat = 52.52, BerlinLon = 13.40;
    private const double TehranLat = 35.69, TehranLon = 51.39;

    [Theory]
    [InlineData("NL", AmsterdamLat, AmsterdamLon)]
    [InlineData("DE", BerlinLat, BerlinLon)]
    [InlineData("IR", TehranLat, TehranLon)]
    [InlineData("JP", 35.68, 139.69)]
    [InlineData("BR", -15.79, -47.88)]
    public void A_city_inside_its_own_country_is_inside(string iso, double latitude, double longitude)
        => Assert.True(WorldMapGeometry.Contains(iso, latitude, longitude));

    [Theory]
    [InlineData("DE", AmsterdamLat, AmsterdamLon)]
    [InlineData("NL", BerlinLat, BerlinLon)]
    [InlineData("IT", 41.15, -8.61)]
    public void A_city_outside_the_country_it_names_is_outside(string iso, double latitude, double longitude)
        => Assert.False(WorldMapGeometry.Contains(iso, latitude, longitude, toleranceKm: 25));

    [Fact]
    public void The_middle_of_the_Atlantic_is_in_no_country()
        => Assert.All(WorldMapCountries.Codes, code => Assert.False(WorldMapGeometry.Contains(code, 30, -40)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("XX")]
    public void A_missing_or_unknown_code_contains_nothing(string? code)
        => Assert.False(WorldMapGeometry.Contains(code, AmsterdamLat, AmsterdamLon));

    [Theory]
    [InlineData(double.NaN, 4.90)]
    [InlineData(52.37, double.NaN)]
    [InlineData(double.PositiveInfinity, 4.90)]
    public void A_non_finite_coordinate_is_inside_nothing(double latitude, double longitude)
        => Assert.False(WorldMapGeometry.Contains("NL", latitude, longitude, toleranceKm: 100));

    [Fact]
    public void The_code_is_read_case_insensitively()
        => Assert.True(WorldMapGeometry.Contains("nl", AmsterdamLat, AmsterdamLon));

    [Fact]
    public void Tolerance_forgives_the_coastline_without_forgiving_the_wrong_country()
    {
        const double latitude = 52.45, longitude = 4.20;
        Assert.False(WorldMapGeometry.Contains("NL", latitude, longitude));
        Assert.True(WorldMapGeometry.Contains("NL", latitude, longitude, toleranceKm: 50));
        Assert.False(WorldMapGeometry.Contains("DE", latitude, longitude, toleranceKm: 50));
    }

    [Fact]
    public void Every_drawable_country_has_one_usable_outline_and_no_extra_outlines()
    {
        Assert.Empty(WorldMapCountries.Codes.Except(WorldMapGeometry.Outlined));
        Assert.Empty(WorldMapGeometry.Outlined.Except(WorldMapCountries.Codes));
    }
}
