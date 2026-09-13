namespace Quesshi.Shared;

/// <summary>The authoring view of a matching question. Matching deliberately has no trivia-only
/// fields such as difficulty, a correct answer, explanation or map data.</summary>
public sealed record MatchingQuestionDto(string Id, string Lang, string MatchingCategoryId, string Prompt,
    string AnswerSource, List<string> Choices, string Status, string Source, MediaDto? Media,
    string? Topic, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int TimesServed);

public sealed record AdminMatchingQuestionPageDto(List<MatchingQuestionDto> Items, long Total);
