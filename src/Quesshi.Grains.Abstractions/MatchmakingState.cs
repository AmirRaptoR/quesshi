using Orleans;

namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchmakingState")]
public sealed class MatchmakingState
{
    [Id(0)] public List<QueueEntry> Waiting { get; set; } = [];
}
