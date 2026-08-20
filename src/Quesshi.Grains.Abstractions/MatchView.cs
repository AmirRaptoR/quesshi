namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchView")]
public sealed record MatchView(
    [property: Id(0)] string Id,
    [property: Id(1)] string Code,
    [property: Id(2)] int Lang,
    [property: Id(3)] string ChallengerId,
    [property: Id(4)] string? OpponentId,
    [property: Id(5)] int State,
    [property: Id(6)] string? WinnerId,
    [property: Id(7)] bool IsDraw,
    [property: Id(8)] DateTimeOffset CreatedAt,
    [property: Id(9)] List<string> QuestionIds,
    [property: Id(10)] List<RunView> Runs);
