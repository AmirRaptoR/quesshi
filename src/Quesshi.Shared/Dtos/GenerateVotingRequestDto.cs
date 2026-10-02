namespace Quesshi.Shared;

/// <summary>Ask the AI provider for one voting language/category/answer-source bucket.</summary>
public sealed record GenerateVotingRequestDto(
    string Lang,
    string CategoryId,
    string AnswerSource,
    int Count);
