namespace Quesshi.Application.Ports;

/// <summary>One billed request to the model, as the provider reported it.</summary>
/// <param name="Purpose">What the call was for — "generate", "illustrate" or "duplicates".</param>
public sealed record AiCall(
    string Id,
    DateTimeOffset At,
    string Model,
    string Purpose,
    int PromptTokens,
    int CompletionTokens,
    decimal Cost);
