namespace Quesshi.Web.Services;

/// <summary>
/// When the round clock's tick cue should fire. Live.razor's own ticker redraws every 250ms (see its
/// <c>_ticker</c>), so "once per second, for each of the last three seconds" cannot just mean "call
/// this whenever the remaining count is small" — that would fire the click up to four times over the
/// same second. This turns a raw remaining-seconds reading into a one-shot event: true only the first
/// time the count is seen at a new value inside the window, by comparing it against the last value
/// already ticked for.
/// </summary>
public static class SoundTicks
{
    /// <summary>
    /// The window is the countdown's last three whole seconds — 3, 2 and 1 — not zero (time's already
    /// up by then, and <c>TimeUp</c>'s own handling takes over) and not the seconds before it.
    /// </summary>
    public static bool ShouldTick(int remainingSeconds, int? lastTicked)
        => remainingSeconds is >= 1 and <= 3 && remainingSeconds != lastTicked;
}
