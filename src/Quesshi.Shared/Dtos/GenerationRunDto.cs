namespace Quesshi.Shared;

public sealed record GenerationRunDto(string Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int Requested, int Inserted, int Rejected, string? Error);
