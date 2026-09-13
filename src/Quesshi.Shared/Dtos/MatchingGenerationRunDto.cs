namespace Quesshi.Shared;

public sealed record MatchingGenerationRunDto(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Lang,
    string CategoryId,
    string AnswerSource,
    int Requested,
    int Inserted,
    int Rejected,
    string? Error);
