using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Live.razor's ticker redraws every 250ms, so the tick cue needs its own one-shot rule rather than
/// firing whenever the remaining count happens to look small — see <see cref="SoundTicks"/>'s own remarks.
/// </summary>
public class SoundTicksTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(0)]
    public void Outside_the_last_three_seconds_it_never_ticks(int remaining)
        => Assert.False(SoundTicks.ShouldTick(remaining, lastTicked: null));

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(1)]
    public void The_first_reading_of_each_of_the_last_three_seconds_ticks(int remaining)
        => Assert.True(SoundTicks.ShouldTick(remaining, lastTicked: null));

    /// <summary>The same second read again a quarter-second later — the 250ms ticker's ordinary
    /// case — must not tick a second time.</summary>
    [Fact]
    public void The_same_second_read_again_does_not_tick_twice()
        => Assert.False(SoundTicks.ShouldTick(remainingSeconds: 3, lastTicked: 3));

    /// <summary>The clock moving on to the next second inside the window ticks again.</summary>
    [Fact]
    public void The_next_second_inside_the_window_ticks_again()
        => Assert.True(SoundTicks.ShouldTick(remainingSeconds: 2, lastTicked: 3));
}
