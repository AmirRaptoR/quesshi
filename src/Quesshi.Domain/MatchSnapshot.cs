namespace Quesshi.Domain;

/// <summary>A whole match reduced to plain data, so storage never has to know about the rules.</summary>
public sealed record MatchSnapshot(
    string Id, string Code, Language Lang, string ChallengerId, string? OpponentId,
    List<string> QuestionIds, MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    string? WinnerId, bool IsDraw, Dictionary<string, RunSnapshot> Runs);
