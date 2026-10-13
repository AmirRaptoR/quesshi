using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Shared;

namespace Quesshi.Kinds.MapPick;

public sealed class MapPickKind : Kind<MapPickPayload, MapPickResolved, MapPickCard, MapPickAnswer, MapPickStored, MapPickReveal>
{
    private const double RadiusComparisonEpsilonKm = 1e-9;
    public override string Id => "map_pick";
    public override int Version => 1;
    public override string Family => "trivia";
    public override bool Gradable => true;
    protected override JsonSerializerContext SerializerContext => MapPickJsonContext.Default;

    public override MapPickPayload Upgrade(int from, JsonElement payload)
    {
        if (from != Version) throw new NotSupportedException($"map_pick v{from} cannot be upgraded by map_pick v{Version}.");
        return JsonSerializer.Deserialize(payload, MapPickJsonContext.Default.MapPickPayload)
            ?? throw new JsonException("Map pick payload is invalid.");
    }

    public override Snapshot<MapPickCard, MapPickReveal, MapPickStored> UpgradeSnapshot(int from, RawSnapshot old)
    {
        if (from != Version) throw new NotSupportedException($"map_pick v{from} cannot be upgraded by map_pick v{Version}.");
        return new(
            JsonSerializer.Deserialize(old.Card, MapPickJsonContext.Default.MapPickCard) ?? throw new JsonException("Map pick card is invalid."),
            JsonSerializer.Deserialize(old.Reveal, MapPickJsonContext.Default.MapPickReveal) ?? throw new JsonException("Map pick reveal is invalid."),
            old.Answers.Select(answer => JsonSerializer.Deserialize(answer, MapPickJsonContext.Default.MapPickStored) ?? throw new JsonException("Map pick stored answer is invalid.")).ToArray());
    }

    public override IReadOnlyList<Problem> Validate(MapPickPayload payload, ValidateEnv env)
    {
        var problems = new List<Problem>();
        if (payload.BaseLayer is not (MapBaseLayer.Blank or MapBaseLayer.Borders))
            problems.Add(new("baseLayer.invalid", "Map base layer must be blank or borders."));

        if (payload.Target is null)
        {
            problems.Add(new("target.required", "Map target is required."));
            return problems;
        }

        var target = payload.Target;
        if (target.Shape is MapTargetKind.Country)
        {
            if (!IsCountryCode(target.CountryCode) || !WorldMapCountries.Codes.Contains(target.CountryCode!))
                problems.Add(new("target.country.invalid", "Map country target must be a drawable ISO alpha-2 code."));
            if (target.Latitude is not null || target.Longitude is not null || target.RadiusKm is not null)
                problems.Add(new("target.shape", "Country targets cannot carry point values."));
        }
        else if (target.Shape is MapTargetKind.Point)
        {
            if (target.CountryCode is not null || target.Latitude is null || target.Longitude is null || target.RadiusKm is null)
                problems.Add(new("target.shape", "Point targets require only latitude, longitude and radius."));
            else
            {
                if (!Geo.IsValidLatitude(target.Latitude.Value)) problems.Add(new("target.latitude.invalid", "Latitude must be finite and between -90 and 90."));
                if (!Geo.IsValidLongitude(target.Longitude.Value)) problems.Add(new("target.longitude.invalid", "Longitude must be finite and between -180 and 180."));
                if (!double.IsFinite(target.RadiusKm.Value) || target.RadiusKm.Value < MapTolerance.MinKm || target.RadiusKm.Value > MapTolerance.MaxKm)
                    problems.Add(new("target.radius.invalid", "Radius must be finite and between 10 and 2000 km."));
            }
        }
        else problems.Add(new("target.shape", "Map target shape must be country or point."));

        return problems;
    }

    public override MapPickResolved Resolve(MapPickPayload payload, ResolveContext context) =>
        new(payload.Target ?? throw new InvalidOperationException("Map target is required."));

    public override MapPickCard Card(Item<MapPickPayload, MapPickResolved> item) =>
        new(item.Payload.BaseLayer, item.Resolved.Target.Shape);

    public override MapPickReveal Reveal(Item<MapPickPayload, MapPickResolved> item) => new(item.Resolved.Target);

    public override MapPickStored Normalize(Item<MapPickPayload, MapPickResolved> item, MapPickAnswer answer)
    {
        var target = item.Resolved.Target;
        if (answer.Shape != target.Shape) throw new MalformedAnswer("Map answer shape must match the target shape.");

        if (answer.Shape is MapTargetKind.Country)
        {
            if (!IsCountryCode(answer.CountryCode) || !WorldMapCountries.Codes.Contains(answer.CountryCode!)
                || answer.Latitude is not null || answer.Longitude is not null)
                throw new MalformedAnswer("Map country answer must be one drawable uppercase ISO alpha-2 code.");
            return MapPickStored.Country(answer.CountryCode!);
        }

        if (answer.Shape is MapTargetKind.Point)
        {
            if (answer.CountryCode is not null || answer.Latitude is null || answer.Longitude is null
                || !Geo.IsValidLatitude(answer.Latitude.Value) || !Geo.IsValidLongitude(answer.Longitude.Value))
                throw new MalformedAnswer("Map point answer must be finite latitude and longitude in range.");
            return MapPickStored.Point(answer.Latitude.Value, answer.Longitude.Value);
        }

        throw new MalformedAnswer("Map answer shape must be country or point.");
    }

    public override Grade Grade(Item<MapPickPayload, MapPickResolved> item, MapPickStored stored)
    {
        var target = item.Resolved.Target;
        var correct = target.Shape == stored.Shape && target.Shape switch
        {
            MapTargetKind.Country => string.Equals(target.CountryCode, stored.CountryCode, StringComparison.Ordinal),
            MapTargetKind.Point => Geo.DistanceKm(target.Latitude!.Value, target.Longitude!.Value, stored.Latitude!.Value, stored.Longitude!.Value)
                <= target.RadiusKm!.Value + RadiusComparisonEpsilonKm,
            _ => false
        };
        return Quesshi.Shared.Grade.Of(correct ? 1 : 0);
    }

    private static bool IsCountryCode(string? code) => code is { Length: 2 } && code.All(char.IsAsciiLetterUpper);
}

[JsonConverter(typeof(JsonStringEnumConverter<MapBaseLayer>))]
public enum MapBaseLayer { Blank, Borders }

[JsonConverter(typeof(JsonStringEnumConverter<MapTargetKind>))]
public enum MapTargetKind { Country, Point }

public sealed record MapPickPayload([property: JsonRequired] MapBaseLayer? BaseLayer, [property: JsonRequired] MapTarget? Target);
public sealed record MapPickResolved(MapTarget Target);
public sealed record MapPickCard(MapBaseLayer? BaseLayer, MapTargetKind? Shape);
public sealed record MapPickReveal(MapTarget Target);
public sealed record MapPickAnswer([property: JsonRequired] MapTargetKind? Shape, string? CountryCode = null, double? Latitude = null, double? Longitude = null);
public sealed record MapPickStored(MapTargetKind Shape, string? CountryCode = null, double? Latitude = null, double? Longitude = null)
{
    public static MapPickStored Country(string code) => new(MapTargetKind.Country, code);
    public static MapPickStored Point(double latitude, double longitude) => new(MapTargetKind.Point, null, latitude, longitude);
}
public sealed record MapTarget([property: JsonRequired] MapTargetKind? Shape, string? CountryCode = null, double? Latitude = null, double? Longitude = null, double? RadiusKm = null);

[JsonSerializable(typeof(MapPickPayload))]
[JsonSerializable(typeof(MapPickResolved))]
[JsonSerializable(typeof(MapPickCard))]
[JsonSerializable(typeof(MapPickAnswer))]
[JsonSerializable(typeof(MapPickStored))]
[JsonSerializable(typeof(MapPickReveal))]
[JsonSerializable(typeof(Snapshot<MapPickCard, MapPickReveal, MapPickStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class MapPickJsonContext : JsonSerializerContext;
