namespace Quesshi.Grains.Abstractions;

/// <summary>
/// <see cref="Result"/> is a <c>Quesshi.Domain.LiveChallengeResult</c> carried as <c>int</c>, the same
/// way <see cref="ILiveMatchGrain.JoinAsync"/> carries <c>LiveJoinResult</c>. <see cref="MatchId"/> is
/// set only when <see cref="Result"/> is <c>Accepted</c>.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveChallengeAcceptResult")]
public sealed record LiveChallengeAcceptResult(
    [property: Id(0)] int Result,
    [property: Id(1)] string? MatchId,
    [property: Id(2)] string? ChallengerId);
