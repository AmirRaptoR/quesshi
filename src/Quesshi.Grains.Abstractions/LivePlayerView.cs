namespace Quesshi.Grains.Abstractions;

/// <summary>One player's running total. Score and Correct count revealed rounds only — the open round
/// leaks through arithmetic otherwise.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LivePlayerView")]
public sealed record LivePlayerView(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Score,
    [property: Id(2)] int Correct,
    [property: Id(3)] int MissStreak);
