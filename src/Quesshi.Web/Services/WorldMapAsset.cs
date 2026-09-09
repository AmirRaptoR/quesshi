using System.Text.RegularExpressions;

namespace Quesshi.Web.Services;

/// <summary>One country as the bundled map draws it: its ISO code, its English name, and its path data.</summary>
/// <param name="Iso">ISO 3166-1 alpha-2, upper-case — the same spelling an answer string uses.</param>
/// <param name="Name">
/// The country's name in English, taken from the <c>&lt;title&gt;</c> the generator wrote into each
/// path. It is English in all three languages, and that is a deliberate, documented shortfall rather
/// than an oversight: a localised country-name dataset is ~175 names in three languages needing a
/// review pass, and <c>docs/sorting-and-map-questions.md</c> defers it by name ("a labelled map
/// layer ... needs a review pass, since a wrong country name in Persian is the kind of error that
/// stays wrong quietly"). The confirmation bar shows the ISO code beside the name for exactly this
/// reason — a Persian player who does not read "Netherlands" still recognises "NL".
/// </param>
/// <param name="D">The SVG path data, in viewBox units, straight out of the asset.</param>
public sealed record MapCountry(string Iso, string Name, string D);

/// <summary>
/// Loads <c>wwwroot/maps/world.svg</c> once per session and hands out its countries.
/// <para>
/// Once, and shared: the file is 150 KB, every map question in a duel draws the same one, and a
/// component that fetched it on each round would re-download it mid-question over whatever
/// connection a phone happens to have. Registered scoped, which for a WebAssembly app is the whole
/// session — the same reasoning <c>Program.cs</c> gives for <c>LobbyClient</c>.
/// </para>
/// <para>
/// The paths are parsed out and re-emitted as ordinary Blazor elements rather than injected as raw
/// markup, so a tap on a country is a plain <c>@onclick</c> on the element that was hit. That is
/// what keeps country selection out of JavaScript entirely: no hit-testing, no
/// <c>elementFromPoint</c>, no second copy of "which country is this" to keep in step with the one
/// the browser already computed by dispatching the event.
/// </para>
/// </summary>
public sealed partial class WorldMapAsset(HttpClient http)
{
    /// <summary>The asset's own path, relative to the app base — the same file
    /// <c>Quesshi.Shared.WorldMapCountries</c> embeds and parses its code set from.</summary>
    public const string Url = "maps/world.svg";

    private IReadOnlyList<MapCountry>? _countries;
    private Task<IReadOnlyList<MapCountry>>? _loading;

    /// <summary>
    /// The countries, fetched on the first call and cached after it. The in-flight task is cached
    /// too, not just the result: two map components rendering in the same frame — a reveal showing
    /// the target beside the player's pick, for one — would otherwise both miss the empty cache and
    /// fetch the file twice.
    /// </summary>
    public Task<IReadOnlyList<MapCountry>> LoadAsync()
    {
        if (_countries is { } loaded) return Task.FromResult(loaded);

        return _loading ??= FetchAsync();
    }

    private async Task<IReadOnlyList<MapCountry>> FetchAsync()
    {
        try
        {
            var countries = Parse(await http.GetStringAsync(Url));
            _countries = countries;
            return countries;
        }
        catch
        {
            // A map question with no map is unanswerable, but taking the whole duel down with an
            // exception on the render path is worse: the component shows its "map unavailable"
            // state, the round runs out, and the rest of the duel carries on. The failed task is
            // cleared so a later round gets a fresh attempt rather than inheriting this one.
            _loading = null;
            return [];
        }
    }

    /// <summary>
    /// Pulls the countries out of the asset's markup. Pure and public so it is tested against the
    /// real shipped file (the test project copies <c>maps/world.svg</c> into its output for exactly
    /// this, the same way it does the i18n bundles) rather than against a fixture that could agree
    /// with a parser and disagree with the file.
    /// <para>
    /// Anchored to what <c>tools/map/generate_world_map.py</c> actually emits — two upper-case ASCII
    /// letters, then the path data, then a <c>&lt;title&gt;</c>. Anything else in the file is not a
    /// country this understands, and is skipped rather than guessed at.
    /// </para>
    /// </summary>
    public static List<MapCountry> Parse(string svg)
        => [.. CountryPath().Matches(svg).Select(m => new MapCountry(m.Groups[1].Value, m.Groups[3].Value, m.Groups[2].Value))];

    [GeneratedRegex("""<path id="([A-Z]{2})"[^>]*\sd="([^"]+)"[^>]*>(?:<title>([^<]*)</title>)?""")]
    private static partial Regex CountryPath();
}
