using Quesshi.Shared;

namespace Quesshi.Web.Tests;

/// <summary>
/// The generator's one real defence against a model that names a city in one country and then gives
/// the coordinates of another (see <see cref="WorldMapGeometry"/>). Like
/// <see cref="WorldMapCountriesTests"/> this runs against the actual embedded asset rather than a
/// fixture, because the whole value of the check is that it agrees with the map the game draws.
/// </summary>
public class WorldMapGeometryTests
{
    // Real capitals, to a couple of decimals. Precise enough to be unambiguous, coarse enough that
    // nobody has to believe a particular gazetteer.
    private const double AmsterdamLat = 52.37, AmsterdamLon = 4.90;
    private const double BerlinLat = 52.52, BerlinLon = 13.40;
    private const double TehranLat = 35.69, TehranLon = 51.39;

    [Theory]
    [InlineData("NL", AmsterdamLat, AmsterdamLon)]
    [InlineData("DE", BerlinLat, BerlinLon)]
    [InlineData("IR", TehranLat, TehranLon)]
    [InlineData("JP", 35.68, 139.69)]
    [InlineData("BR", -15.79, -47.88)]
    public void A_city_inside_its_own_country_is_inside(string iso, double lat, double lon)
        => Assert.True(WorldMapGeometry.Contains(iso, lat, lon));

    /// <summary>
    /// The failure the check exists for: right city, right country name, coordinates from somewhere
    /// else entirely. Amsterdam's point is nowhere near Germany, and no tolerance rescues it.
    /// </summary>
    [Theory]
    [InlineData("DE", AmsterdamLat, AmsterdamLon)]
    [InlineData("NL", BerlinLat, BerlinLon)]
    [InlineData("IT", 41.15, -8.61)]
    public void A_city_outside_the_country_it_names_is_outside(string iso, double lat, double lon)
        => Assert.False(WorldMapGeometry.Contains(iso, lat, lon, toleranceKm: 25));

    [Fact]
    public void The_middle_of_the_Atlantic_is_in_no_country()
        => Assert.All(WorldMapCountries.Codes, code => Assert.False(WorldMapGeometry.Contains(code, 30, -40)));

    [Fact]
    public void A_country_the_map_does_not_draw_contains_nothing()
        => Assert.False(WorldMapGeometry.Contains("XX", AmsterdamLat, AmsterdamLon));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_code_contains_nothing(string? code)
        => Assert.False(WorldMapGeometry.Contains(code, AmsterdamLat, AmsterdamLon));

    /// <summary>
    /// NaN is inside nothing. It passes a naive range check and then makes every comparison false,
    /// which is exactly how a question nobody can answer gets stored with nothing reporting a fault.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 4.90)]
    [InlineData(52.37, double.NaN)]
    [InlineData(double.PositiveInfinity, 4.90)]
    public void A_non_finite_coordinate_is_inside_nothing(double lat, double lon)
        => Assert.False(WorldMapGeometry.Contains("NL", lat, lon, toleranceKm: 100));

    [Fact]
    public void The_code_is_read_case_insensitively()
        => Assert.True(WorldMapGeometry.Contains("nl", AmsterdamLat, AmsterdamLon));

    /// <summary>
    /// The tolerance's reason for existing, pinned rather than described: a point in the North Sea
    /// just off the Dutch coast is outside the 1:110m outline, and a slack measured in tens of
    /// kilometres lets a true coastal city through where a strict test would reject it. The same
    /// point is still not in Germany, which is what keeps the slack honest.
    /// </summary>
    [Fact]
    public void The_tolerance_forgives_the_coastline_without_forgiving_the_wrong_country()
    {
        const double justOffTheDutchCoast = 52.45, atSea = 4.20;

        Assert.False(WorldMapGeometry.Contains("NL", justOffTheDutchCoast, atSea));
        Assert.True(WorldMapGeometry.Contains("NL", justOffTheDutchCoast, atSea, toleranceKm: 50));
        Assert.False(WorldMapGeometry.Contains("DE", justOffTheDutchCoast, atSea, toleranceKm: 50));
    }

    /// <summary>
    /// Every code the map can draw has an outline this can test against. Without this, a country
    /// whose path failed to parse would quietly reject every city question written about it, and the
    /// first sign would be an empty bucket nobody could explain.
    /// </summary>
    [Fact]
    public void Every_country_the_map_draws_has_a_usable_outline()
        => Assert.Empty(WorldMapCountries.Codes.Except(WorldMapGeometry.Outlined));

    [Fact]
    public void Nothing_has_an_outline_that_is_not_a_country_the_map_draws()
        => Assert.Empty(WorldMapGeometry.Outlined.Except(WorldMapCountries.Codes));
}
