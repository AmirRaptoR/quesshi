namespace Quesshi.Grains.Abstractions;

/// <summary>
/// The whole live duel as one record, redacted for whoever asked. Every deadline is absolute
/// server time, not a remaining-seconds count, so a reconnecting client can redraw the right arc
/// without a per-round sync.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveView")]
public sealed record LiveView(
    [property: Id(0)] string Id,
    [property: Id(1)] string ChallengerId,
    [property: Id(2)] string? OpponentId,
    [property: Id(3)] int State,
    [property: Id(4)] int Phase,
    [property: Id(5)] DateTimeOffset? PhaseEndsAt,
    [property: Id(6)] int RoundIndex,
    [property: Id(7)] int TotalRounds,
    [property: Id(8)] List<LivePlayerView> Players,
    [property: Id(9)] List<LiveRoundResultView> Rounds,
    [property: Id(10)] string? WinnerId,
    [property: Id(11)] bool IsDraw,
    [property: Id(12)] string? AbandonedBy,
    [property: Id(13)] DateTimeOffset CreatedAt,
    [property: Id(14)] DateTimeOffset? EndedAt);
