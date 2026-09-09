namespace Quesshi.Web.Services;

/// <summary>
/// A contiguous run of difficulty levels, from <see cref="Low"/> to <see cref="High"/> inclusive —
/// what issue #89's two-handle slider produces, and a deliberate simplification of what the picker
/// it replaces could express. The old chips could pick any subset at all, holes included ("very easy
/// and very hard, nothing between"), which nobody asked for and which reads back as an unspeakable
/// sentence: <see cref="DuelSettingsSummary"/> already gave up describing such a set as anything but
/// its two ends. A range says the same thing the summary was already saying, and is the one shape a
/// slider can actually express, so the two now agree by construction rather than by approximation.
///
/// The levels themselves are <c>Quesshi.Domain.Difficulty</c>'s 1..5, mirrored here the same way
/// <see cref="DuelSettingsSummary.LevelCount"/> and DuelSettingsPicker's own list mirror it —
/// Quesshi.Web has no reference to Quesshi.Domain.
/// </summary>
public readonly record struct DifficultyRange(int Low, int High)
{
    public const int Easiest = 1;
    public const int Hardest = DuelSettingsSummary.LevelCount;

    /// <summary>Every level: the default, and the ramp the game was designed around — a duel that
    /// starts gentle and finishes hard. Narrowing is the exception, which is why the slider opens
    /// spanning its whole track.</summary>
    public static readonly DifficultyRange Whole = new(Easiest, Hardest);

    public bool IsWhole => Low <= Easiest && High >= Hardest;

    /// <summary>
    /// What a stored <c>DuelSettings.Levels</c> means as a range. Empty is the whole ramp — that is
    /// what the server itself does with an empty list, and what "no preference" has always meant on
    /// this API. Anything else is read end to end, so a set with a hole in it (which a build from
    /// before this issue could have written into localStorage, and which the API still accepts)
    /// widens to the contiguous range covering it rather than being thrown away.
    /// </summary>
    public static DifficultyRange From(IEnumerable<int>? levels)
    {
        // Out-of-range values are dropped rather than trusted, for the same reason DuelSettingsSummary
        // drops them: these lists cross the wire, and a level outside 1..5 has no name to render and
        // would only ever widen the range wrongly.
        var picked = (levels ?? []).Where(l => l is >= Easiest and <= Hardest).ToList();

        return picked.Count == 0 ? Whole : new DifficultyRange(picked.Min(), picked.Max());
    }

    /// <summary>
    /// The list to send. The whole ramp goes as an empty list rather than as all five: the two mean
    /// the same thing to every endpoint that takes them, and the empty form is the one that keeps
    /// meaning "whatever the game's default ramp is" if that ramp ever gains a sixth level.
    /// </summary>
    public List<int> Levels() => IsWhole ? [] : [.. Enumerable.Range(Low, High - Low + 1)];

    /// <summary>
    /// The easy handle moved. The two handles push each other rather than blocking: dragging the
    /// easy handle past the hard one carries the hard one along instead of stopping dead at it.
    ///
    /// That is a rendering decision as much as a feel one. The handles are two native
    /// <c>input[type=range]</c>s, and Blazor only patches an attribute it rendered differently last
    /// time — so a handler that *rejected* a value (clamping 4 back to 3) would leave the browser's
    /// own thumb sitting at 4 with nothing in the diff to pull it back, and the drawn range and the
    /// control would disagree until the next accepted move. Pushing never rejects: the model always
    /// ends up holding exactly what the input reported, and the other input's value genuinely
    /// changed, so it patches. Either way the handles cannot cross — <see cref="Low"/> is never
    /// above <see cref="High"/> — which is the invariant that actually matters.
    /// </summary>
    public DifficultyRange WithLow(int value)
    {
        var low = Math.Clamp(value, Easiest, Hardest);
        return new DifficultyRange(low, Math.Max(High, low));
    }

    /// <summary>The hard handle moved; see <see cref="WithLow"/> for why it pushes.</summary>
    public DifficultyRange WithHigh(int value)
    {
        var high = Math.Clamp(value, Easiest, Hardest);
        return new DifficultyRange(Math.Min(Low, high), high);
    }

    /// <summary>Where a handle sits along the track, 0-100. The slider draws its own dimming from
    /// this rather than from the inputs, so what is greyed out and what the model holds can never
    /// drift apart.</summary>
    public static double Position(int level)
        => (Math.Clamp(level, Easiest, Hardest) - Easiest) * 100.0 / (Hardest - Easiest);
}
