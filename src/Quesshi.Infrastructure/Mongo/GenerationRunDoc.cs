using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class GenerationRunDoc
{
    [BsonId] public string Id { get; set; } = "";
    public int Family { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? Lang { get; set; }
    public string? CategoryId { get; set; }
    public int? AnswerSource { get; set; }
    public int Requested { get; set; }
    public int Inserted { get; set; }
    public int Rejected { get; set; }
    public string? Error { get; set; }

    public static GenerationRunDoc From(GenerationRun r) => new()
    {
        Id = r.Id,
        Family = (int)r.Family,
        StartedAt = r.StartedAt.UtcDateTime,
        FinishedAt = r.FinishedAt?.UtcDateTime,
        Requested = r.Requested,
        Inserted = r.Inserted,
        Rejected = r.Rejected,
        Error = r.Error
    };

    public GenerationRun ToDomain()
    {
        if ((QuestionFamily)Family != QuestionFamily.Trivia)
            throw new InvalidOperationException("Cannot convert a voting generation run to a trivia generation run.");
        return new GenerationRun(Id, new DateTimeOffset(StartedAt, TimeSpan.Zero),
            FinishedAt is null ? null : new DateTimeOffset(FinishedAt.Value, TimeSpan.Zero), Requested, Inserted, Rejected, Error);
    }

    public static GenerationRunDoc From(VotingGenerationRun r) => new()
    {
        Id = r.Id,
        Family = (int)r.Family,
        StartedAt = r.StartedAt.UtcDateTime,
        FinishedAt = r.FinishedAt?.UtcDateTime,
        Lang = (int)r.Lang,
        CategoryId = r.CategoryId,
        AnswerSource = (int)r.AnswerSource,
        Requested = r.Requested,
        Inserted = r.Inserted,
        Rejected = r.Rejected,
        Error = r.Error
    };

    public VotingGenerationRun ToVoting()
    {
        if ((QuestionFamily)Family != QuestionFamily.Voting)
            throw new InvalidOperationException("Cannot convert a trivia generation run to a voting generation run.");
        return new VotingGenerationRun(Id, new DateTimeOffset(StartedAt, TimeSpan.Zero),
            FinishedAt is null ? null : new DateTimeOffset(FinishedAt.Value, TimeSpan.Zero),
            (Language)(Lang ?? throw new InvalidOperationException("Voting run language is missing.")),
            CategoryId ?? throw new InvalidOperationException("Voting run category is missing."),
            (VotingAnswerSource)(AnswerSource ?? throw new InvalidOperationException("Voting answer source is missing.")),
            Requested, Inserted, Rejected, Error);
    }
}
