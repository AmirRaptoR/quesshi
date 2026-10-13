using System.Globalization;
using System.Text.Json;
using Quesshi.Kinds.MapPick;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class MapPickKindTests
{
    private readonly MapPickKind _kind = new();

    [Fact]
    public void Metadata_and_payload_validation_follow_the_map_pick_contract()
    {
        Assert.Equal(("map_pick", 1, "trivia", true), (_kind.Id, _kind.Version, _kind.Family, _kind.Gradable));
        Assert.Empty(_kind.Validate(CountryPayload("DE"), new ValidateEnv("en")));
        Assert.Empty(_kind.Validate(PointPayload(0, 179.9, 100), new ValidateEnv("en")));

        Assert.Contains(_kind.Validate(CountryPayload("ZZ"), new ValidateEnv("en")), x => x.Code == "target.country.invalid");
        Assert.Contains(_kind.Validate(new(null, null), new ValidateEnv("en")), x => x.Code == "baseLayer.invalid");
        Assert.Contains(_kind.Validate(new(MapBaseLayer.Blank, new(MapTargetKind.Country, "DE", 0, null, null)), new ValidateEnv("en")), x => x.Code == "target.shape");
        Assert.Contains(_kind.Validate(PointPayload(double.NaN, 0, 50), new ValidateEnv("en")), x => x.Code == "target.latitude.invalid");
        Assert.Contains(_kind.Validate(PointPayload(0, 181, 50), new ValidateEnv("en")), x => x.Code == "target.longitude.invalid");
        Assert.Contains(_kind.Validate(PointPayload(0, 0, 9.99), new ValidateEnv("en")), x => x.Code == "target.radius.invalid");
    }

    [Fact]
    public void Resolve_is_stable_card_does_not_leak_the_target_and_reveal_identifies_it()
    {
        var first = Item(CountryPayload("DE"));
        var second = Item(CountryPayload("NL"));

        Assert.Equal(first.Resolved, _kind.Resolve(first.Payload, new ResolveContext("session", 3, [])));
        Assert.Equal(_kind.Card(first), _kind.Card(second));
        Assert.Equal(new(MapBaseLayer.Blank, MapTargetKind.Country), _kind.Card(first));
        Assert.Equal("DE", _kind.Reveal(first).Target.CountryCode);

        var card = JsonSerializer.SerializeToElement(_kind.Card(first), MapPickJsonContext.Default.MapPickCard).GetRawText();
        Assert.DoesNotContain("DE", card, StringComparison.Ordinal);
        Assert.DoesNotContain("latitude", card, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("radius", card, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_accepts_only_the_matching_well_formed_answer_shape()
    {
        IKindRuntime runtime = _kind;
        var countryPayload = Json("{\"baseLayer\":\"Blank\",\"target\":{\"shape\":\"Country\",\"countryCode\":\"DE\"}}");
        var countryResolved = runtime.Resolve(countryPayload, new ResolveContext("session", 0, []));

        Assert.Equal("{\"shape\":\"Country\",\"countryCode\":\"DE\",\"latitude\":null,\"longitude\":null}",
            runtime.Normalize(countryPayload, countryResolved, Json("{\"shape\":\"Country\",\"countryCode\":\"DE\"}"))?.GetRawText());
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(countryPayload, countryResolved, Json("{\"shape\":\"Country\",\"countryCode\":\"de\"}")));
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(countryPayload, countryResolved, Json("{\"shape\":\"Country\",\"countryCode\":\"DEU\"}")));
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(countryPayload, countryResolved, Json("{\"shape\":\"Point\",\"latitude\":0,\"longitude\":0}")));

        var pointPayload = Json("{\"baseLayer\":\"Borders\",\"target\":{\"shape\":\"Point\",\"latitude\":0,\"longitude\":179.9,\"radiusKm\":50}}");
        var pointResolved = runtime.Resolve(pointPayload, new ResolveContext("session", 0, []));
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(pointPayload, pointResolved, Json("{\"shape\":\"Point\",\"latitude\":\"0,1\",\"longitude\":0}")));
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(pointPayload, pointResolved, Json("{\"shape\":\"Point\",\"latitude\":91,\"longitude\":0}")));
        Assert.Throws<MalformedAnswer>(() => _kind.Normalize(Item(PointPayload(0, 0, 50)), new(MapTargetKind.Point, null, double.NaN, 0)));
        Assert.Null(runtime.Normalize(pointPayload, pointResolved, null));
        Assert.Equal(0, runtime.Grade(pointPayload, pointResolved, null).Credit);
    }

    [Theory]
    [InlineData(0d, 0d, 50d)]
    [InlineData(89d, 0d, 50d)]
    [InlineData(0d, 179.9d, 50d)]
    public void Point_credit_is_inclusive_at_the_radius_across_map_edge_cases(double latitude, double longitude, double radiusKm)
    {
        var item = Item(PointPayload(latitude, longitude, radiusKm));
        var kmPerDegree = Math.PI * Geo.EarthRadiusKm / 180;
        var points = Math.Abs(longitude) > 170
            ? PointAtLongitudeOffset(latitude, longitude, kmPerDegree, radiusKm)
            : PointAtLatitudeOffset(latitude, longitude, kmPerDegree, radiusKm);

        Assert.Equal(1, _kind.Grade(item, MapPickStored.Point(points.Inside.Latitude, points.Inside.Longitude)).Credit);
        Assert.Equal(1, _kind.Grade(item, MapPickStored.Point(points.Boundary.Latitude, points.Boundary.Longitude)).Credit);
        Assert.Equal(0, _kind.Grade(item, MapPickStored.Point(points.Outside.Latitude, points.Outside.Longitude)).Credit);
    }

    [Fact]
    public void Country_credit_requires_exact_code_equality()
    {
        var item = Item(CountryPayload("DE"));
        Assert.Equal(1, _kind.Grade(item, MapPickStored.Country("DE")).Credit);
        Assert.Equal(0, _kind.Grade(item, MapPickStored.Country("NL")).Credit);
    }

    [Fact]
    public void Map_helpers_are_culture_invariant_and_agree_with_the_bundled_asset()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            Assert.Equal("52.37,4.9", Geo.Format(52.37, 4.9));
            Assert.True(Geo.TryParse("52.37,4.9", out var latitude, out var longitude));
            Assert.Equal(52.37, latitude, 6);
            Assert.Equal(4.9, longitude, 6);
            Assert.False(Geo.TryParse("52,37,4,9", out _, out _));
            Assert.False(Geo.TryParse("NaN,4.9", out _, out _));
        }
        finally { CultureInfo.CurrentCulture = before; }

        var svg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "world.svg"));
        var codesInAsset = System.Text.RegularExpressions.Regex.Matches(svg, "data-iso=\"([A-Z]{2})\"").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(codesInAsset.OrderBy(code => code), WorldMapCountries.Codes.OrderBy(code => code));
        Assert.Equal(WorldMapCountries.Codes.OrderBy(code => code), WorldMapGeometry.Outlined.OrderBy(code => code));
        Assert.Equal((4.9, -52.37), WorldMapProjection.Project(52.37, 4.9));
    }

    private static MapPickPayload CountryPayload(string code) => new(MapBaseLayer.Blank, new(MapTargetKind.Country, code));
    private static MapPickPayload PointPayload(double latitude, double longitude, double radiusKm) => new(MapBaseLayer.Borders, new(MapTargetKind.Point, null, latitude, longitude, radiusKm));
    private Item<MapPickPayload, MapPickResolved> Item(MapPickPayload payload) => new(payload, _kind.Resolve(payload, new ResolveContext("session", 0, [])));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static ((double Latitude, double Longitude) Inside, (double Latitude, double Longitude) Boundary, (double Latitude, double Longitude) Outside)
        PointAtLatitudeOffset(double latitude, double longitude, double kmPerDegree, double radiusKm) =>
            ((latitude + (radiusKm - 1) / kmPerDegree, longitude), (latitude + radiusKm / kmPerDegree, longitude), (latitude + (radiusKm + 1) / kmPerDegree, longitude));

    private static ((double Latitude, double Longitude) Inside, (double Latitude, double Longitude) Boundary, (double Latitude, double Longitude) Outside)
        PointAtLongitudeOffset(double latitude, double longitude, double kmPerDegree, double radiusKm) =>
            ((latitude, NormalizeLongitude(longitude + (radiusKm - 1) / kmPerDegree)), (latitude, NormalizeLongitude(longitude + radiusKm / kmPerDegree)), (latitude, NormalizeLongitude(longitude + (radiusKm + 1) / kmPerDegree)));

    private static double NormalizeLongitude(double longitude) => longitude > 180 ? longitude - 360 : longitude;
}
