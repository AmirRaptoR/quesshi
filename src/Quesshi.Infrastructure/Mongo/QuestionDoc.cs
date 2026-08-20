using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class QuestionDoc
{
    [BsonId] public string Id { get; set; } = "";
    public int Lang { get; set; }
    public string CategoryId { get; set; } = "";
    public int Level { get; set; }
    public string Prompt { get; set; } = "";
    public List<string> Choices { get; set; } = [];
    public int CorrectIndex { get; set; }
    public int MediaKind { get; set; }
    public string MediaUrl { get; set; } = "";
    public string? MediaAttribution { get; set; }
    public string? Explanation { get; set; }
    public string? Topic { get; set; }
    public int Status { get; set; }
    public int Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TimesServed { get; set; }
    public int TimesCorrect { get; set; }
    public List<ReportDoc> Reports { get; set; } = [];

    /// <summary>Denormalised so the database can sort and filter on it without unwinding the array.</summary>
    public int ReportCount { get; set; }

    public static QuestionDoc From(Question q) => new()
    {
        Id = q.Id,
        Lang = (int)q.Lang,
        CategoryId = q.CategoryId,
        Level = (int)q.Level,
        Prompt = q.Prompt,
        Choices = [.. q.Choices],
        CorrectIndex = q.CorrectIndex,
        MediaKind = (int)q.Media.Kind,
        MediaUrl = q.Media.Url,
        MediaAttribution = q.Media.Attribution,
        Explanation = q.Explanation,
        Topic = q.Topic,
        Status = (int)q.Status,
        Source = (int)q.Source,
        CreatedAt = q.CreatedAt.UtcDateTime,
        TimesServed = q.TimesServed,
        TimesCorrect = q.TimesCorrect,
        Reports = [.. q.Reports.Select(r => new ReportDoc { PlayerId = r.PlayerId, Reason = (int)r.Reason, At = r.At.UtcDateTime })],
        ReportCount = q.ReportCount
    };

    public Question ToDomain() => Question.Restore(Id, (Language)Lang, CategoryId, (Difficulty)Level, Prompt, Choices,
        CorrectIndex, new MediaRef((MediaKind)MediaKind, MediaUrl, MediaAttribution), Explanation,
        (QuestionStatus)Status, (QuestionSource)Source, new DateTimeOffset(CreatedAt, TimeSpan.Zero), TimesServed, TimesCorrect,
        Reports.Select(r => new QuestionReport(r.PlayerId, (ReportReason)r.Reason, new DateTimeOffset(r.At, TimeSpan.Zero))),
        Topic);
}
