using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>
/// Asks a model through OpenRouter's OpenAI-compatible chat endpoint for a batch of questions.
/// The JSON schema does the shape enforcement; <c>TopUpQuestionBank</c> still validates every item,
/// because a schema-valid question can still be a bad question.
/// </summary>
public sealed class OpenRouterQuestionGenerator(
    OpenRouterOptions options,
    IHttpClientFactory http,
    QuestionPromptBuilder prompts,
    IAiSpendLog spend,
    IIdFactory ids,
    IClock clock,
    ILogger<OpenRouterQuestionGenerator> logger) : IQuestionGenerator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(Language lang, Category category, Difficulty level,
        int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => AskAsync(prompts.User(lang, category, level, count, avoid), QuestionSchema.ResponseFormat, "generate", lang, category, level, ct);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateIllustratedAsync(Language lang, Category category, Difficulty level,
        int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => AskAsync(prompts.Illustrated(lang, category, level, count, avoid), IllustratedSchema.ResponseFormat, "illustrate", lang, category, level, ct);

    private async Task<IReadOnlyList<GeneratedQuestion>> AskAsync(string userPrompt, object schema, string purpose,
        Language lang, Category category, Difficulty level, CancellationToken ct)
    {
        if (!IsConfigured) return [];

        using var client = http.CreateClient();
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        client.DefaultRequestHeaders.Add("HTTP-Referer", options.AppUrl);
        client.DefaultRequestHeaders.Add("X-Title", options.AppName);
        client.Timeout = TimeSpan.FromMinutes(3);

        var request = new
        {
            model = options.Model,
            max_tokens = options.MaxTokens,
            messages = new[]
            {
                new { role = "system", content = prompts.System() },
                new { role = "user", content = userPrompt }
            },
            response_format = schema,

            // Makes OpenRouter report tokens and the actual charge on the response, which is what
            // the admin panel's spend panel adds up.
            usage = new { include = true }
        };

        var response = await client.PostAsJsonAsync("chat/completions", request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}: {Trim(payload)}");

        if (UsageReader.Read(payload, ids.NewId(), clock.Now, options.Model, purpose) is { } call)
            await spend.RecordAsync(call, ct);

        var content = ExtractContent(payload);
        if (string.IsNullOrWhiteSpace(content))
        {
            logger.LogWarning("OpenRouter returned no content for {Lang}/{Category}/{Level}", lang, category.Id, level);
            return [];
        }

        return Parse(content, lang, category, level);
    }

    private IReadOnlyList<GeneratedQuestion> Parse(string content, Language lang, Category category, Difficulty level)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<Batch>(Unwrap(content), Json);
            return batch?.Questions is null
                ? []
                : [.. batch.Questions.Select(q => new GeneratedQuestion(q.Prompt ?? "", q.Choices ?? [], q.CorrectIndex, q.Explanation)
                    { Subject = q.Subject, Aspect = q.Aspect })];
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OpenRouter returned malformed JSON for {Lang}/{Category}/{Level}", lang, category.Id, level);
            return [];
        }
    }

    private static string? ExtractContent(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
    }

    /// <summary>Not every model honours the schema; some still wrap the JSON in a markdown fence.</summary>
    private static string Unwrap(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith('{'))
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start) text = text[start..(end + 1)];
        }
        return text;
    }

    private static string Trim(string value) => value.Length > 400 ? value[..400] + "…" : value;

    private sealed record Batch(List<Item>? Questions);

    private sealed record Item(string? Prompt, List<string>? Choices, int CorrectIndex, string? Explanation,
        string? Subject, string? Aspect);
}
