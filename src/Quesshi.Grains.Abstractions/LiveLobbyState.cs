namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveLobbyState")]
public sealed class LiveLobbyState
{
    [Id(0)] public List<LiveQueueEntry> Waiting { get; set; } = [];
    [Id(1)] public List<LiveChallengeView> Challenges { get; set; } = [];
}
