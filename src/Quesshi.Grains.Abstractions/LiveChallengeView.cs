namespace Quesshi.Grains.Abstractions;

/// <summary>Everything a target needs to render an invitation banner, and everything accepting needs to build the duel.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveChallengeView")]
public sealed record LiveChallengeView(
    [property: Id(0)] string ChallengeId,
    [property: Id(1)] string ChallengerId,
    [property: Id(2)] string TargetId,
    [property: Id(3)] int Lang,
    [property: Id(4)] int QuestionCount,
    [property: Id(5)] List<string> CategoryIds,
    [property: Id(6)] List<int> Levels,
    [property: Id(7)] DateTimeOffset SentAt,
    [property: Id(8)] DateTimeOffset ExpiresAt);
