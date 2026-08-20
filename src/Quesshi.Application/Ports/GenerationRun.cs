namespace Quesshi.Application.Ports;

public sealed record GenerationRun(string Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int Requested, int Inserted, int Rejected, string? Error);
