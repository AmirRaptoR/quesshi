namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.QueueEntry")]
public sealed record QueueEntry(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Lang,
    [property: Id(2)] string MatchId,
    [property: Id(3)] DateTimeOffset QueuedAt);
