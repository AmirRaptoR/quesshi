namespace Quesshi.Shared;

/// <summary>Payload accepted by the matching question authoring endpoint.</summary>
public sealed record SaveMatchingQuestionDto(string? Id, string Lang, string MatchingCategoryId, string Prompt,
    string AnswerSource, List<string> Choices, string? MediaKind, string? MediaUrl,
    string? MediaAttribution, string? Subject, string? Aspect, string Status);
