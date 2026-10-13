using Quesshi.Kinds.MapPick;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class MapTargetTests
{
    private readonly MapPickKind _kind = new();

    [Fact]
    public void A_country_target_requires_one_drawable_uppercase_alpha_2_code_and_no_point_values()
    {
        Assert.Empty(ValidateTarget(new(MapTargetKind.Country, "DE")));
        foreach (var code in new[] { "", " ", "D", "de", "DEU", "D1", "۳۴", "ZZ" })
            Assert.Contains(ValidateTarget(new(MapTargetKind.Country, code)), problem => problem.Code == "target.country.invalid");

        Assert.Contains(ValidateTarget(new(MapTargetKind.Country, "DE", 0, null, null)), problem => problem.Code == "target.shape");
    }

    [Theory]
    [InlineData(90.0001)]
    [InlineData(-90.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_impossible_latitude_is_refused(double latitude)
        => Assert.Contains(ValidatePayload(Point(latitude, 0, 50)), problem => problem.Code == "target.latitude.invalid");

    [Theory]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_impossible_longitude_is_refused(double longitude)
        => Assert.Contains(ValidatePayload(Point(0, longitude, 50)), problem => problem.Code == "target.longitude.invalid");

    [Theory]
    [InlineData(9.99)]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(2000.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_radius_outside_the_shared_bounds_is_refused(double radiusKm)
        => Assert.Contains(ValidatePayload(Point(52.37, 4.9, radiusKm)), problem => problem.Code == "target.radius.invalid");

    [Fact]
    public void Point_target_requires_exactly_its_coordinates_and_radius_and_allows_range_endpoints()
    {
        Assert.Empty(ValidatePayload(Point(90, 180, MapTolerance.MinKm)));
        Assert.Empty(ValidatePayload(Point(-90, -180, MapTolerance.MaxKm)));
        Assert.Contains(ValidateTarget(new(MapTargetKind.Point, "DE", 0, 0, 50)), problem => problem.Code == "target.shape");
        Assert.Contains(ValidateTarget(new(MapTargetKind.Point, null, null, 0, 50)), problem => problem.Code == "target.shape");
    }

    [Fact]
    public void Targets_with_the_same_values_are_equal_and_resolve_without_rewriting_the_target()
    {
        var target = Point(52.37, 4.9, 50).Target!;
        var duplicate = Point(52.37, 4.9, 50).Target!;
        Assert.Equal(target, duplicate);
        Assert.Equal(target, _kind.Resolve(new(MapBaseLayer.Borders, target), new ResolveContext("session", 0, [])).Target);
    }

    private IReadOnlyList<Problem> ValidateTarget(MapTarget target) => ValidatePayload(new(MapBaseLayer.Blank, target));
    private IReadOnlyList<Problem> ValidatePayload(MapPickPayload payload) => _kind.Validate(payload, new ValidateEnv("en"));
    private static MapPickPayload Point(double latitude, double longitude, double radiusKm) =>
        new(MapBaseLayer.Borders, new(MapTargetKind.Point, null, latitude, longitude, radiusKm));
}
