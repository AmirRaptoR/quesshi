namespace Quesshi.Application.Ports;

/// <summary>A matching question returned by the model, before validation or de-duplication.</summary>
public sealed record GeneratedMatchingQuestion(
    string Prompt,
    IReadOnlyList<string> Choices,
    string? Subject,
    string? Aspect);
