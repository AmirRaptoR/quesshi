namespace Quesshi.Grains.Abstractions;

/// <summary>
/// One player's place in <see cref="ILiveLobbyGrain"/>'s queue. Categories and levels ride along
/// unused unless this entry turns out to be the one an arriving player is matched against — then
/// they are what the resulting duel is built from, because whoever queued first sets the terms.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveQueueEntry")]
public sealed record LiveQueueEntry(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Lang,
    [property: Id(2)] int QuestionCount,
    [property: Id(3)] List<string> Categories,
    [property: Id(4)] List<int> Levels,
    [property: Id(5)] DateTimeOffset QueuedAt);
