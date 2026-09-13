using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public sealed record MatchingGenerationRun(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    Language Lang,
    string CategoryId,
    MatchingAnswerSource AnswerSource,
    int Requested,
    int Inserted,
    int Rejected,
    string? Error);
