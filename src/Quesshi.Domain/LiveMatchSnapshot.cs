namespace Quesshi.Domain;

/// <summary>A whole live duel reduced to plain data, so storage never has to know about the rules.</summary>
public sealed record LiveMatchSnapshot(
    string Id, string ChallengerId, string? OpponentId, List<string> QuestionIds,
    MatchState State, LivePhase Phase, DateTimeOffset? PhaseEndsAt, List<LiveRoundSnapshot> Rounds,
    Dictionary<string, int> MissStreaks, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    string? WinnerId, bool IsDraw, string? AbandonedBy);
