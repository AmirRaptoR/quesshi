namespace Quesshi.Shared;

/// <summary>Ask the AI provider for one matching language/category/answer-source bucket.</summary>
public sealed record GenerateMatchingRequestDto(
    string Lang,
    string CategoryId,
    string AnswerSource,
    int Count);
