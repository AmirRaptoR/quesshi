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
    ILogger<OpenRouterQuestionGenerator> logger) : IQuestionGenerator, IMatchingQuestionGenerator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.ApiKey);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(Language lang, Category category, Difficulty level,
        int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => AskAsync(prompts.User(lang, category, level, count, avoid), QuestionSchema.ResponseFormat, "generate", lang, category, level, QuestionKind.Choice, ct);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateIllustratedAsync(Language lang, Category category, Difficulty level,
        int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => AskAsync(prompts.Illustrated(lang, category, level, count, avoid), IllustratedSchema.ResponseFormat, "illustrate", lang, category, level, QuestionKind.Choice, ct);

    /// <summary>
    /// Same transport, same spend accounting, same de-duplication downstream — a different prompt
    /// and a different schema. The purpose string is what tells the spend panel a sorting batch
    /// apart from a choice one after the fact.
    /// </summary>
    public Task<IReadOnlyList<GeneratedQuestion>> GenerateSortAsync(Language lang, Category category, Difficulty level,
        int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => AskAsync(prompts.Sort(lang, category, level, count, avoid), SortSchema.ResponseFormat, "sort", lang, category, level, QuestionKind.Sort, ct);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateMapAsync(Language lang, Category category, Difficulty level,
        int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => AskAsync(prompts.Map(lang, category, level, count, avoid), MapSchema.ResponseFormat, "map", lang, category, level, QuestionKind.Map, ct);

    Task<IReadOnlyList<GeneratedMatchingQuestion>> IMatchingQuestionGenerator.GenerateAsync(Language lang,
        MatchingCategory category, MatchingAnswerSource answerSource, int count,
        IReadOnlyCollection<string> avoid, CancellationToken ct)
        => AskMatchingAsync(prompts.Matching(lang, category, answerSource, count, avoid),
            MatchingQuestionSchema.ResponseFormat(answerSource), lang, category, answerSource, ct);

    private async Task<IReadOnlyList<GeneratedQuestion>> AskAsync(string userPrompt, object schema, string purpose,
        Language lang, Category category, Difficulty level, QuestionKind kind, CancellationToken ct)
    {
        var content = await RequestAsync(prompts.System(), userPrompt, schema, purpose, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            logger.LogWarning("OpenRouter returned no content for {Lang}/{Category}/{Level}", lang, category.Id, level);
            return [];
        }

        return Parse(content, lang, category, level, kind);
    }

    private async Task<IReadOnlyList<GeneratedMatchingQuestion>> AskMatchingAsync(string userPrompt,
        object schema, Language lang, MatchingCategory category, MatchingAnswerSource answerSource,
        CancellationToken ct)
    {
        var purpose = answerSource == MatchingAnswerSource.Fixed
            ? "matching-fixed"
            : "matching-participants";
        var content = await RequestAsync(prompts.MatchingSystem(), userPrompt, schema, purpose, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            logger.LogWarning("OpenRouter returned no matching content for {Lang}/{Category}/{AnswerSource}",
                lang, category.Id, answerSource);
            return [];
        }

        return ParseMatching(content, lang, category, answerSource);
    }

    private async Task<string?> RequestAsync(string systemPrompt, string userPrompt, object schema,
        string purpose, CancellationToken ct)
    {
        if (!IsConfigured) return null;

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
                new { role = "system", content = systemPrompt },
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

        return ExtractContent(payload);
    }

    /// <summary>
    /// One <c>Item</c> record covers all three schemas: the fields a kind does not use simply arrive
    /// null, and reading them into a shape the pipeline already understands beats three parsers that
    /// would each have to be kept in step with the same <c>GeneratedQuestion</c>.
    /// <para>
    /// Nothing here decides whether a candidate is any good — that is <c>TopUpQuestionBank</c>'s job
    /// and it does not trust this layer. This only converts.
    /// </para>
    /// </summary>
    private IReadOnlyList<GeneratedQuestion> Parse(string content, Language lang, Category category, Difficulty level, QuestionKind kind)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<Batch>(Unwrap(content), Json);
            return batch?.Questions is null ? [] : [.. batch.Questions.Select(q => ToCandidate(q, kind))];
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OpenRouter returned malformed JSON for {Lang}/{Category}/{Level}", lang, category.Id, level);
            return [];
        }
    }

    private IReadOnlyList<GeneratedMatchingQuestion> ParseMatching(string content, Language lang,
        MatchingCategory category, MatchingAnswerSource answerSource)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<Batch>(Unwrap(content), Json);
            return batch?.Questions is null
                ? []
                : [.. batch.Questions.Select(q => new GeneratedMatchingQuestion(
                    q.Prompt ?? "",
                    answerSource == MatchingAnswerSource.Fixed ? q.Choices ?? [] : [],
                    q.Subject,
                    q.Aspect))];
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OpenRouter returned malformed matching JSON for {Lang}/{Category}/{AnswerSource}",
                lang, category.Id, answerSource);
            return [];
        }
    }

    private static GeneratedQuestion ToCandidate(Item q, QuestionKind kind)
    {
        // A map question carries no choices at all — its answer is the target — so anything the
        // model volunteered in that field is dropped here rather than travelling to a validator that
        // would only reject the whole question for it.
        var candidate = new GeneratedQuestion(q.Prompt ?? "", kind == QuestionKind.Map ? [] : q.Choices ?? [],
            kind == QuestionKind.Choice ? q.CorrectIndex : 0, q.Explanation)
        {
            Subject = q.Subject,
            Aspect = q.Aspect
        };

        if (kind != QuestionKind.Map) return candidate;

        return candidate with
        {
            // Anything that is not the literal string "city" is read as a country question, which is
            // the safer default of the two: a country target is checked against the map's own code
            // set, while a city target believed on a mistyped discriminator would be a point with no
            // coordinates behind it.
            TargetShape = string.Equals(q.TargetKind?.Trim(), "city", StringComparison.OrdinalIgnoreCase)
                ? MapTargetKind.City
                : MapTargetKind.Country,
            CountryCode = q.CountryCode,
            Latitude = q.Latitude,
            Longitude = q.Longitude,
            RadiusKm = q.RadiusKm
        };
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
        string? Subject, string? Aspect, string? TargetKind, string? CountryCode,
        double? Latitude, double? Longitude, double? RadiusKm);
}
