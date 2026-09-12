using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Matching content is intentionally not represented by <see cref="QuestionDoc"/>.</remarks>
[BsonIgnoreExtraElements]
public sealed class MatchingQuestionDoc
{
    [BsonId] public string Id { get; set; } = "";
    public int Lang { get; set; }
    public string MatchingCategoryId { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int AnswerSource { get; set; }
    public List<string> FixedChoices { get; set; } = [];
    public int MediaKind { get; set; }
    public string MediaUrl { get; set; } = "";
    public string? MediaAttribution { get; set; }
    public string? Topic { get; set; }
    public int Status { get; set; }
    public int Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int TimesServed { get; set; }

    public static MatchingQuestionDoc From(MatchingQuestion q) => new()
    {
        Id = q.Id,
        Lang = (int)q.Lang,
        MatchingCategoryId = q.MatchingCategoryId,
        Prompt = q.Prompt,
        AnswerSource = (int)q.AnswerSource,
        FixedChoices = [.. q.FixedChoices],
        MediaKind = (int)q.Media.Kind,
        MediaUrl = q.Media.Url,
        MediaAttribution = q.Media.Attribution,
        Topic = q.Topic,
        Status = (int)q.Status,
        Source = (int)q.Source,
        CreatedAt = q.CreatedAt.UtcDateTime,
        UpdatedAt = q.UpdatedAt.UtcDateTime,
        TimesServed = q.TimesServed
    };

    public MatchingQuestion ToDomain() => MatchingQuestion.Restore(
        Id, (Language)Lang, MatchingCategoryId, Prompt, (MatchingAnswerSource)AnswerSource,
        FixedChoices, new MediaRef((MediaKind)MediaKind, MediaUrl, MediaAttribution),
        (QuestionStatus)Status, (QuestionSource)Source, Topic,
        new DateTimeOffset(CreatedAt, TimeSpan.Zero), new DateTimeOffset(UpdatedAt, TimeSpan.Zero), TimesServed);
}
