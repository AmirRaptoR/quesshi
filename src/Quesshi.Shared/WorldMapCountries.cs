using System.Text.RegularExpressions;

namespace Quesshi.Shared;

/// <summary>
/// The ISO 3166-1 alpha-2 codes for every country that actually has a <c>&lt;path&gt;</c> in the
/// bundled world map (<c>wwwroot/maps/world.svg</c>). A <c>Map</c> question's country target is
/// only ever valid if it is in this set — the code that draws the map and the code that
/// validates a question against it must agree, or a question can be authored against a country
/// the map has no way to highlight.
///
/// <para>
/// That agreement is enforced structurally rather than by discipline: this class does not
/// hand-list the codes (a hand-written list is exactly the kind of thing that quietly drifts the
/// day someone edits the SVG and forgets the list two files away). Instead the SVG itself is
/// embedded into this assembly as a build-time <c>EmbeddedResource</c> — see
/// <c>Quesshi.Shared.csproj</c>, which points straight at the file under
/// <c>Quesshi.Web/wwwroot/maps</c> rather than a copy of it — and the codes are parsed out of its
/// actual <c>data-iso</c> attributes the first time <see cref="Codes"/> is touched. Edit the map
/// and the set of valid targets changes with it; there is nothing else to keep in sync.
/// </para>
/// </summary>
public static partial class WorldMapCountries
{
    private static readonly Lazy<IReadOnlySet<string>> LazyCodes = new(Load);

    /// <summary>Every ISO 3166-1 alpha-2 code the map can draw, e.g. <c>"IR"</c>, <c>"NL"</c>.</summary>
    public static IReadOnlySet<string> Codes => LazyCodes.Value;

    private static IReadOnlySet<string> Load()
    {
        var svg = WorldMapSvg.Text;

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DataIsoAttribute().Matches(svg))
            codes.Add(match.Groups[1].Value);

        if (codes.Count == 0)
            throw new InvalidOperationException(
                "world.svg was embedded but contained no data-iso attributes — is it the right file?");

        return codes;
    }

    // Matches this project's own output (see tools/map/generate_world_map.py):
    // <path id="XX" data-iso="XX" ...>. Anchored to exactly two uppercase ASCII letters, which is
    // the whole of ISO 3166-1 alpha-2 — anything else in the attribute would be a bug in the
    // generator, not a country code to trust.
    [GeneratedRegex("data-iso=\"([A-Z]{2})\"")]
    private static partial Regex DataIsoAttribute();
}
