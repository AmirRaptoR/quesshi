namespace Quesshi.Domain;

/// <summary>Storage shape for an unresolved matching question retained for future slots.</summary>
public sealed record MatchingQuestionSnapshot(
    string Id,
    Language Lang,
    string MatchingCategoryId,
    string Prompt,
    MatchingAnswerSource AnswerSource,
    List<string> FixedChoices,
    MediaRef Media,
    QuestionStatus Status,
    QuestionSource Source,
    string? Topic,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TimesServed);
