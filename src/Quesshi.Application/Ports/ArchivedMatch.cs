using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public sealed record ArchivedMatch(
    string Id, string Code, Language Lang, string ChallengerId, string? OpponentId, string? WinnerId, bool IsDraw,
    int ChallengerScore, int OpponentScore, MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    List<string> QuestionIds);
