using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class GenerationRunDoc
{
    [BsonId] public string Id { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int Requested { get; set; }
    public int Inserted { get; set; }
    public int Rejected { get; set; }
    public string? Error { get; set; }

    public static GenerationRunDoc From(GenerationRun r) => new()
    {
        Id = r.Id, StartedAt = r.StartedAt.UtcDateTime, FinishedAt = r.FinishedAt?.UtcDateTime,
        Requested = r.Requested, Inserted = r.Inserted, Rejected = r.Rejected, Error = r.Error
    };

    public GenerationRun ToDomain() => new(Id, new DateTimeOffset(StartedAt, TimeSpan.Zero),
        FinishedAt is null ? null : new DateTimeOffset(FinishedAt.Value, TimeSpan.Zero), Requested, Inserted, Rejected, Error);
}
