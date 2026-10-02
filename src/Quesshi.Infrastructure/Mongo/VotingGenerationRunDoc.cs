using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

[BsonIgnoreExtraElements]
public sealed class VotingGenerationRunDoc
{
    [BsonId] public string Id { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int Lang { get; set; }
    public string CategoryId { get; set; } = "";
    public int AnswerSource { get; set; }
    public int Requested { get; set; }
    public int Inserted { get; set; }
    public int Rejected { get; set; }
    public string? Error { get; set; }

    public static VotingGenerationRunDoc From(VotingGenerationRun run) => new()
    {
        Id = run.Id,
        StartedAt = run.StartedAt.UtcDateTime,
        FinishedAt = run.FinishedAt?.UtcDateTime,
        Lang = (int)run.Lang,
        CategoryId = run.CategoryId,
        AnswerSource = (int)run.AnswerSource,
        Requested = run.Requested,
        Inserted = run.Inserted,
        Rejected = run.Rejected,
        Error = run.Error
    };

    public VotingGenerationRun ToDomain() => new(
        Id,
        new DateTimeOffset(StartedAt, TimeSpan.Zero),
        FinishedAt is null ? null : new DateTimeOffset(FinishedAt.Value, TimeSpan.Zero),
        (Language)Lang,
        CategoryId,
        (VotingAnswerSource)AnswerSource,
        Requested,
        Inserted,
        Rejected,
        Error);
}
