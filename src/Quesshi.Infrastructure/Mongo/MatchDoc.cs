using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class MatchDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public int Lang { get; set; }
    public string ChallengerId { get; set; } = "";
    public string? OpponentId { get; set; }
    public string? WinnerId { get; set; }
    public bool IsDraw { get; set; }
    public int ChallengerScore { get; set; }
    public int OpponentScore { get; set; }
    public int State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public List<string> QuestionIds { get; set; } = [];

    public static MatchDoc From(ArchivedMatch m) => new()
    {
        Id = m.Id, Code = m.Code, Lang = (int)m.Lang, ChallengerId = m.ChallengerId, OpponentId = m.OpponentId, WinnerId = m.WinnerId,
        IsDraw = m.IsDraw, ChallengerScore = m.ChallengerScore, OpponentScore = m.OpponentScore, State = (int)m.State,
        CreatedAt = m.CreatedAt.UtcDateTime, EndedAt = m.EndedAt?.UtcDateTime, QuestionIds = m.QuestionIds
    };

    public ArchivedMatch ToDomain() => new(Id, Code, (Language)Lang, ChallengerId, OpponentId, WinnerId, IsDraw,
        ChallengerScore, OpponentScore, (MatchState)State, new DateTimeOffset(CreatedAt, TimeSpan.Zero),
        EndedAt is null ? null : new DateTimeOffset(EndedAt.Value, TimeSpan.Zero), QuestionIds);
}
