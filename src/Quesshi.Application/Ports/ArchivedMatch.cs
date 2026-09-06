using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public sealed record ArchivedMatch(
    string Id, string Code, Language Lang, string ChallengerId, string? OpponentId, string? WinnerId, bool IsDraw,
    int ChallengerScore, int OpponentScore, MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    List<string> QuestionIds,
    /// <summary>
    /// Last and defaulted, so every existing caller keeps compiling and an async duel archived
    /// before live duels existed reads back as what it always was: not one.
    /// </summary>
    bool IsLive = false);
