namespace Quesshi.Grains.Abstractions;

/// <summary>The whole live duel, as JSON. See the ponytail note on MatchGrain.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveMatchStateRecord")]
public sealed class LiveMatchStateRecord
{
    [Id(0)] public string Json { get; set; } = "";
}
