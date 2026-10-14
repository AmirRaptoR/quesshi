using System.Globalization;
using System.Text.RegularExpressions;
using Quesshi.Shared;

namespace Quesshi.Kinds.MapPick.Client;

/// <summary>A location drawn by the map client, either a drawable country or a geographic point.</summary>
public sealed record MapSelection(string? CountryCode, double? Latitude, double? Longitude)
{
    public bool IsCountry => CountryCode is not null;
    public bool IsPoint => Latitude is not null;

    public static MapSelection Country(string code) => new(code.Trim().ToUpperInvariant(), null, null);
    public static MapSelection Point(double latitude, double longitude) => new(null, latitude, longitude);
}

public sealed record MapMark(MapSelection Selection, string Label, bool? Right);
public sealed record MapCountry(string Iso, string Name, string Path);

public static class MapText
{
    public static string CountryName(IReadOnlyList<MapCountry> countries, string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return string.Empty;
        var country = countries.FirstOrDefault(country => string.Equals(country.Iso, iso, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(country?.Name) ? iso : country.Name;
    }

    public static string Coordinates(MapSelection selection) => selection.IsPoint
        ? $"{Degrees(selection.Latitude!.Value)}, {Degrees(selection.Longitude!.Value)}"
        : string.Empty;

    private static string Degrees(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "°";
}

/// <summary>Loads and parses the map's static asset; the task is shared by every lazy map component.</summary>
public sealed partial class WorldMapAsset(HttpClient http)
{
    public const string Url = "_content/Quesshi.Kinds.MapPick.Client/maps/world.svg";
    private static Task<IReadOnlyList<MapCountry>>? loading;

    public Task<IReadOnlyList<MapCountry>> LoadAsync() => loading ??= FetchAsync();

    private async Task<IReadOnlyList<MapCountry>> FetchAsync()
    {
        try { return Parse(await http.GetStringAsync(Url)); }
        catch { loading = null; return []; }
    }

    public static IReadOnlyList<MapCountry> Parse(string svg) =>
        [.. CountryPath().Matches(svg).Select(match => new MapCountry(match.Groups[1].Value, match.Groups[3].Value, match.Groups[2].Value))];

    [GeneratedRegex("""<path id="([A-Z]{2})"[^>]*\sd="([^"]+)"[^>]*>(?:<title>([^<]*)</title>)?""")]
    private static partial Regex CountryPath();
}
