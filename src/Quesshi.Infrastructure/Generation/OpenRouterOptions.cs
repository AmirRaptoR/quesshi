namespace Quesshi.Infrastructure.Generation;

public sealed class OpenRouterOptions
{
    public string? ApiKey { get; set; }

    /// <summary>Any model id OpenRouter exposes, e.g. "google/gemini-2.5-flash" or "google/gemma-3-12b-it".</summary>
    // Writing four-option trivia is not a frontier task, and the bill is per question.
    public string Model { get; set; } = "google/gemini-2.5-flash";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public int MaxTokens { get; set; } = 8000;

    /// <summary>Sent as HTTP-Referer and X-Title; OpenRouter uses them for attribution on its dashboard.</summary>
    public string AppUrl { get; set; } = "https://quesshi.app";
    public string AppName { get; set; } = "Quesshi";
}
