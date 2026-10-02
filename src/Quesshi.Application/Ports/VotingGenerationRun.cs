using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public sealed record VotingGenerationRun(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    Language Lang,
    string CategoryId,
    VotingAnswerSource AnswerSource,
    int Requested,
    int Inserted,
    int Rejected,
    string? Error);
