using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

[BsonIgnoreExtraElements]
public sealed class ContentSettingsDoc
{
    [BsonId] public string Id { get; set; } = "settings";
    public List<string> TriviaCategoryIds { get; set; } = [];
    public List<string> VotingCategoryIds { get; set; } = [];

    public ContentSettings ToDomain() => new(TriviaCategoryIds, VotingCategoryIds);
    public static ContentSettingsDoc From(ContentSettings value) => new()
    {
        TriviaCategoryIds = [.. value.TriviaCategoryIds.Distinct(StringComparer.Ordinal)],
        VotingCategoryIds = [.. value.VotingCategoryIds.Distinct(StringComparer.Ordinal)]
    };
}
