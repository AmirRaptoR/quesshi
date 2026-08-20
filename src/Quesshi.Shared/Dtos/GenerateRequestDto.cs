namespace Quesshi.Shared;

/// <summary>Ask the model for questions in one specific bucket.</summary>
public sealed record GenerateRequestDto(string Lang, string CategoryId, int Level, int Count);
