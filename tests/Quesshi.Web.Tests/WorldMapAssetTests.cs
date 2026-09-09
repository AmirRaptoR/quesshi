using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The map is inlined as ordinary Blazor elements, one per country, so a tap on a country is the
/// browser's own hit test rather than a second implementation of one. That only holds if the parser
/// actually finds every country in the file, so these run against <c>wwwroot/maps/world.svg</c>
/// exactly as it ships — copied into the test output by <c>Quesshi.Web.Tests.csproj</c>, the same way
/// <see cref="WorldMapProjectionTests"/> reads it — rather than against a fixture that could agree
/// with the parser and disagree with the asset.
/// </summary>
public class WorldMapAssetTests
{
    private static readonly string Svg =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "world.svg"));

    private static readonly List<MapCountry> Countries = WorldMapAsset.Parse(Svg);

    [Fact]
    public void Every_country_in_the_asset_is_parsed()
    {
        // The count is not hard-coded here — the point is that the parser and the file agree, not
        // that either matches a number typed into a test. Counting the data-iso attributes is a
        // different route through the same file than the parser takes.
        var attributes = System.Text.RegularExpressions.Regex.Matches(Svg, "data-iso=\"[A-Z]{2}\"").Count;

        Assert.Equal(attributes, Countries.Count);
        Assert.NotEmpty(Countries);
    }

    [Fact]
    public void The_parsed_codes_are_exactly_the_set_the_domain_validates_targets_against()
    {
        // WorldMapCountries.Codes is what Question.Validate rejects an unknown country target with,
        // and it parses the same file from an embedded copy. If these two ever disagreed, a question
        // could be authored against a country the map has no way to draw — or a country the map can
        // draw could be refused.
        Assert.Equal(WorldMapCountries.Codes.OrderBy(c => c), Countries.Select(c => c.Iso).OrderBy(c => c));
    }

    [Theory]
    [InlineData("IR", "Iran")]
    [InlineData("NL", "Netherlands")]
    [InlineData("DE", "Germany")]
    public void A_country_carries_the_name_the_bar_beneath_the_map_shows(string iso, string expectedNameFragment)
    {
        var country = Countries.Single(c => c.Iso == iso);

        Assert.Contains(expectedNameFragment, country.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(country.Name, MapText.CountryName(Countries, iso));

        // Case-insensitively, because a stored target may not be upper-case even though a pick is.
        Assert.Equal(country.Name, MapText.CountryName(Countries, iso.ToLowerInvariant()));
    }

    [Fact]
    public void Every_country_has_path_data_and_a_name()
    {
        Assert.All(Countries, country =>
        {
            Assert.Matches("^[A-Z]{2}$", country.Iso);
            Assert.StartsWith("M", country.D);
            Assert.False(string.IsNullOrWhiteSpace(country.Name), $"{country.Iso} has no <title>.");
        });
    }

    [Fact]
    public void A_code_the_map_cannot_draw_falls_back_to_the_code_itself()
    {
        // The bar has to say something. "ZZ" is more use to a player than an empty row, and it is
        // also what the ISO chip beside the name would have said anyway.
        Assert.Equal("ZZ", MapText.CountryName(Countries, "ZZ"));
        Assert.Equal("", MapText.CountryName(Countries, null));
    }

    [Fact]
    public void A_point_is_described_in_degrees_latitude_first()
    {
        // Two decimals, not the six the wire carries: a tap is precise to tens of kilometres, so
        // the rest of the digits are the pixel grid talking.
        Assert.Equal("52.37°, 4.9°", MapText.Coordinates(MapPick.Point(52.37, 4.9)));
        Assert.Equal("52.82°, 4.07°", MapText.Coordinates(MapPick.Point(52.817797, 4.067797)));

        // A country has no coordinates to describe; the bar shows its name and code instead.
        Assert.Equal("", MapText.Coordinates(MapPick.Country("NL")));
    }

    [Fact]
    public void Nothing_is_parsed_out_of_markup_that_is_not_the_map()
    {
        // A fetch that returned the app's index.html — a stray 404 rewrite, say — must read as "no
        // countries" and let the component show its unavailable state, not as a half-drawn world.
        Assert.Empty(WorldMapAsset.Parse("<html><body><path id=\"nope\" d=\"M0,0Z\"/></body></html>"));
        Assert.Empty(WorldMapAsset.Parse(""));
    }
}
