namespace Quesshi.Application.Ports;

/// <summary>A voting question returned by the model, before validation or de-duplication.</summary>
public sealed record GeneratedVotingQuestion(
    string Prompt,
    IReadOnlyList<string> Choices,
    string? Subject,
    string? Aspect);
