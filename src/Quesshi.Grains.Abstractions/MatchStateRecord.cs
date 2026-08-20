namespace Quesshi.Grains.Abstractions;

/// <summary>The whole match, as JSON. See the ponytail note on MatchGrain.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchStateRecord")]
public sealed class MatchStateRecord
{
    [Id(0)] public string Json { get; set; } = "";
}
