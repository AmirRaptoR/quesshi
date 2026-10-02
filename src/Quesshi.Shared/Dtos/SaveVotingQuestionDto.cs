namespace Quesshi.Shared;

/// <summary>Payload accepted by the voting question authoring endpoint.</summary>
public sealed record SaveVotingQuestionDto(string? Id, string Lang, string? CategoryId, string Prompt,
    string AnswerSource, List<string> Choices, string? MediaKind, string? MediaUrl,
    string? MediaAttribution, string? Subject, string? Aspect, string Status);
