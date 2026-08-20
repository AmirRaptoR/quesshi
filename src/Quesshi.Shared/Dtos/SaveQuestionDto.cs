namespace Quesshi.Shared;

public sealed record SaveQuestionDto(string? Id, string Lang, string CategoryId, int Level, string Prompt,
    List<string> Choices, int CorrectIndex, string? Explanation, string? MediaKind, string? MediaUrl, string Status);
