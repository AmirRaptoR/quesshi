namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveLobbyState")]
public sealed class LiveLobbyState
{
    [Id(0)] public List<LiveQueueEntry> Waiting { get; set; } = [];
    [Id(1)] public List<LiveChallengeView> Challenges { get; set; } = [];
}

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveQueueEntry")]
public sealed record LiveQueueEntry(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Lang,
    [property: Id(2)] DateTimeOffset QueuedAt);
