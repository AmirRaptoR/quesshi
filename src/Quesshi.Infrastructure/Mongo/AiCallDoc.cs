using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

[BsonIgnoreExtraElements]
public sealed class AiCallDoc
{
    [BsonId] public string Id { get; set; } = "";
    public DateTime At { get; set; }
    public string Model { get; set; } = "";
    public string Purpose { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    // Money is decimal everywhere; Decimal128 keeps it that way through the round trip.
    [BsonRepresentation(BsonType.Decimal128)] public decimal Cost { get; set; }

    public static AiCallDoc From(AiCall c) => new()
    {
        Id = c.Id, At = c.At.UtcDateTime, Model = c.Model, Purpose = c.Purpose,
        PromptTokens = c.PromptTokens, CompletionTokens = c.CompletionTokens, Cost = c.Cost
    };
}
