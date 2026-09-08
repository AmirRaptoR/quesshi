namespace Quesshi.Grains.Abstractions;

/// <summary>
/// <see cref="Participants"/> replaced the old <c>ChallengerId</c>/<c>OpponentId</c> pair as part of
/// issue #53's wire-contract widening: index 0 is always the lobby's owner, exactly as
/// <c>Match.Participants</c> itself defines it, and every later seat follows in join order. Like
/// <c>LiveView</c>, this type is never persisted — it is rebuilt fresh from <c>Match</c> on every
/// grain call and handed straight back to the caller in the same process generation that built it —
/// so renumbering its <c>[Id(n)]</c> slots here carries no migration risk.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchView")]
public sealed record MatchView(
    [property: Id(0)] string Id,
    [property: Id(1)] string Code,
    [property: Id(2)] int Lang,
    [property: Id(3)] List<string> Participants,
    [property: Id(4)] int State,
    [property: Id(5)] string? WinnerId,
    [property: Id(6)] bool IsDraw,
    [property: Id(7)] DateTimeOffset CreatedAt,
    [property: Id(8)] List<string> QuestionIds,
    [property: Id(9)] List<RunView> Runs);
