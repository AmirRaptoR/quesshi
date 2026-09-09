using Quesshi.Shared;

namespace Quesshi.Web.Tests;

/// <summary>
/// <see cref="WorldMapCountries.Codes"/> is parsed out of the real, embedded <c>world.svg</c>
/// (see the type's own doc comment and <c>wwwroot/maps/README.md</c>), so this is really a test
/// of the asset via the API the domain's future country-target validator will actually call, not
/// a test of a hand-written fixture.
/// </summary>
public class WorldMapCountriesTests
{
    [Fact]
    public void The_code_set_is_not_empty()
        => Assert.NotEmpty(WorldMapCountries.Codes);

    [Theory]
    [InlineData("IR")]
    [InlineData("NL")]
    [InlineData("DE")]
    [InlineData("US")]
    public void The_code_set_contains(string code)
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
    public void Reading_the_set_twice_returns_the_same_instance()
        // Codes is loaded once and cached (it is parsed from an embedded resource, not free to
        // re-derive on every call); this pins that it stays a set, not a re-allocated list.
        => Assert.Same(WorldMapCountries.Codes, WorldMapCountries.Codes);
}
