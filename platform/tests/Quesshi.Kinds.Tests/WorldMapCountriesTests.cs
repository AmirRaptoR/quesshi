using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class WorldMapCountriesTests
{
    [Fact]
    public void The_code_set_is_not_empty()
        => Assert.NotEmpty(WorldMapCountries.Codes);

    [Theory]
    [InlineData("IR")]
    [InlineData("NL")]
    [InlineData("DE")]
    [InlineData("US")]
    public void The_code_set_contains_drawable_countries(string code)
        => Assert.Contains(code, WorldMapCountries.Codes);

    [Fact]
    public void Every_code_is_exactly_two_uppercase_ascii_letters()
    {
        Assert.All(WorldMapCountries.Codes, code =>
        {
            Assert.Equal(2, code.Length);
            Assert.All(code, c => Assert.True(c is >= 'A' and <= 'Z', $"'{code}' is not two uppercase ASCII letters."));
        });
    }

    [Fact]
    public void Reading_the_set_twice_returns_the_cached_set()
        => Assert.Same(WorldMapCountries.Codes, WorldMapCountries.Codes);

    [Fact]
    public void Bundled_asset_and_drawable_code_set_agree()
    {
        var svg = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "world.svg"));
        var assetCodes = System.Text.RegularExpressions.Regex.Matches(svg, "data-iso=\"([A-Z]{2})\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(assetCodes.OrderBy(code => code), WorldMapCountries.Codes.OrderBy(code => code));
    }
}
