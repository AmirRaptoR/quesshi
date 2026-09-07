namespace Quesshi.Grains.Abstractions;

/// <summary>The whole live duel, as JSON. See the ponytail note on MatchGrain.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveMatchStateRecord")]
public sealed class LiveMatchStateRecord
{
    [Id(0)] public string Json { get; set; } = "";

    /// <summary>
    /// Which participant has pressed Rematch, and when — a flag, not a counter, so a second press by
    /// the same player is a no-op. Kept beside the match's own JSON rather than inside it: readiness
    /// is a grain-level handshake concern, not part of the duel <see cref="LiveMatchSnapshot"/> replays.
    /// </summary>
    [Id(1)] public Dictionary<string, DateTimeOffset> RematchReadyAt { get; set; } = [];
}
