using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>Settings.razor's Sounds switch, persisted as `quesshi.sounds` through window.quesshi's
/// localStorage helpers — see SoundsPreference's own remarks on why "1"/"0" rather than a real bool.</summary>
public class SoundsPreferenceTests
{
    [Fact]
    public void A_key_never_written_defaults_to_on()
        => Assert.True(SoundsPreference.Parse(null));

    [Fact]
    public void An_explicit_zero_is_off()
        => Assert.False(SoundsPreference.Parse("0"));

    [Fact]
    public void An_explicit_one_is_on()
        => Assert.True(SoundsPreference.Parse("1"));

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public void Serialising_then_parsing_round_trips(bool enabled, string stored)
    {
        Assert.Equal(stored, SoundsPreference.Serialize(enabled));
        Assert.Equal(enabled, SoundsPreference.Parse(stored));
    }
}
