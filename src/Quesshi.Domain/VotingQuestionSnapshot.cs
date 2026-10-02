namespace Quesshi.Domain;

/// <summary>Storage shape for an unresolved voting question retained for future slots.</summary>
public sealed record VotingQuestionSnapshot(
    string Id,
    Language Lang,
    string VotingCategoryId,
    string Prompt,
    VotingAnswerSource AnswerSource,
    List<string> FixedChoices,
    MediaRef Media,
    QuestionStatus Status,
    QuestionSource Source,
    string? Topic,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TimesServed);
