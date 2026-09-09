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

    /// <summary>
    /// Nullable on purpose, and deliberately *not* defaulted to 0 in this class the way every other
    /// int-backed enum column here is. A document written before <c>QuestionKind</c> existed has no
    /// <c>Kind</c> element at all, and BSON deserialisation leaves a missing field exactly as it
    /// found the property — null — so <see cref="ToDomain"/> can tell "genuinely absent" apart from
    /// "explicitly stored as Choice" and coalesce the former on purpose instead of by enum-zero
    /// coincidence. See the comment there.
    /// </summary>
    public int? Kind { get; set; }

    /// <summary>
    /// The map target's shape, or null when this question has no target at all (Choice and Sort).
    /// Only one of <see cref="TargetCountryCode"/> or the lat/lon/radius trio is ever populated —
    /// which one is exactly what this field says.
    /// </summary>
    public int? TargetShape { get; set; }
    public string? TargetCountryCode { get; set; }
    public double? TargetLatitude { get; set; }
    public double? TargetLongitude { get; set; }
    public double? TargetRadiusKm { get; set; }

    /// <summary>Null for anything but a map question, same as <see cref="Question.BaseLayer"/>.</summary>
    public int? BaseLayer { get; set; }

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
        ReportCount = q.ReportCount,
        Kind = (int)q.Kind,
        TargetShape = q.Target is null ? null : (int)q.Target.Shape,
        TargetCountryCode = q.Target?.CountryCode,
        TargetLatitude = q.Target?.Latitude,
        TargetLongitude = q.Target?.Longitude,
        TargetRadiusKm = q.Target?.RadiusKm,
        BaseLayer = q.BaseLayer is null ? null : (int)q.BaseLayer.Value
    };

    public Question ToDomain()
    {
        // A legacy document -- written before sorting and map questions existed -- has no Kind
        // element at all, which deserialises to null here rather than 0. Every such row predates
        // QuestionKind entirely and so is a Choice question by definition; this coalesce says that
        // explicitly rather than relying on QuestionKind.Choice happening to be the enum's zero
        // value, which is the same mistake for an enum that a magic number is for anything else.
        var kind = Kind is null ? QuestionKind.Choice : (QuestionKind)Kind.Value;

        MapTarget? target = TargetShape switch
        {
            (int)MapTargetKind.Country => MapTarget.Restore(MapTargetKind.Country, TargetCountryCode, null, null, null),
            (int)MapTargetKind.City => MapTarget.Restore(MapTargetKind.City, null, TargetLatitude, TargetLongitude, TargetRadiusKm),
            _ => null
        };

        return Question.Restore(Id, (Language)Lang, CategoryId, (Difficulty)Level, Prompt, Choices,
            CorrectIndex, new MediaRef((MediaKind)MediaKind, MediaUrl, MediaAttribution), Explanation,
            (QuestionStatus)Status, (QuestionSource)Source, new DateTimeOffset(CreatedAt, TimeSpan.Zero), TimesServed, TimesCorrect,
            Reports.Select(r => new QuestionReport(r.PlayerId, (ReportReason)r.Reason, new DateTimeOffset(r.At, TimeSpan.Zero))),
            Topic, kind, target, BaseLayer is null ? null : (MapBaseLayer)BaseLayer.Value);
    }
}
