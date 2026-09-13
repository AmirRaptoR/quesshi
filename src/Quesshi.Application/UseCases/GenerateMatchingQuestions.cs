using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>
/// Trust boundary for AI-authored matching content. The provider only proposes candidates; this
/// service pins their language, category and answer source, validates them with the matching domain,
/// rejects duplicate wording/topics, and writes only through the matching repository.
/// </summary>
public sealed class GenerateMatchingQuestions(
    IMatchingQuestionRepository questions,
    IMatchingCategoryRepository categories,
    IMatchingQuestionGenerator generator,
    IMatchingGenerationLog log,
    IClock clock,
    IIdFactory ids,
    MatchingGenerationOptions options,
    ILogger<GenerateMatchingQuestions>? logger = null)
{
    public async Task<MatchingGenerationRun> RunAsync(Language lang, string categoryId,
        MatchingAnswerSource answerSource, int count, CancellationToken ct = default)
    {
        var run = new MatchingGenerationRun(ids.NewId(), clock.Now, null, lang, categoryId,
            answerSource, count, 0, 0, null);

        if (!Enum.IsDefined(answerSource))
            return await FinishAsync(run with { Requested = 0, Error = "bad_answer_source" }, ct);

        if (await categories.GetAsync(categoryId, ct) is not { } category)
            return await FinishAsync(run with { Requested = 0, Error = "unknown_category" }, ct);

        if (!category.IsActive)
            return await FinishAsync(run with { Requested = 0, Error = "inactive_category" }, ct);

        if (!generator.IsConfigured)
            return await FinishAsync(run with { Requested = 0, Error = "generator_not_configured" }, ct);

        var wanted = Math.Clamp(count, 1, Math.Max(1, Math.Min(20, options.MaxBatchSize)));

        try
        {
            var index = PromptIndex.FromPrompts(await questions.ExistingPromptsAsync(lang, category.Id, ct));
            var topics = (await questions.ExistingTopicsAsync(lang, ct)).ToHashSet();
            var candidates = await generator.GenerateAsync(
                lang, category, answerSource, wanted, index.Recent(60), ct);

            var accepted = new List<MatchingQuestion>();
            var rejected = 0;

            foreach (var candidate in candidates.Take(wanted))
            {
                var prompt = candidate.Prompt?.Trim() ?? string.Empty;
                var topic = TopicKey.From(candidate.Subject, candidate.Aspect);

                // Generated rows must be identifiable. Unlike a hand-authored question, an AI row
                // without a topic cannot be protected from a differently-worded duplicate later.
                if (topic is null || topics.Contains(topic) || index.Contains(prompt))
                {
                    rejected++;
                    continue;
                }

                try
                {
                    var question = MatchingQuestion.Create(ids.NewId(), lang, category.Id, prompt,
                        answerSource, candidate.Choices, clock.Now, source: QuestionSource.Ai,
                        status: options.AutoApprove ? QuestionStatus.Approved : QuestionStatus.Pending,
                        topic: topic);
                    accepted.Add(question);
                    index.Add(question.Prompt);
                    topics.Add(topic);
                }
                catch (ArgumentException ex)
                {
                    logger?.LogInformation("Rejecting a generated matching question: {Reason}", ex.Message);
                    rejected++;
                }
            }

            var stored = accepted.Count == 0 ? 0 : await questions.UpsertManyAsync(accepted, ct);
            return await FinishAsync(run with
            {
                Requested = wanted,
                Inserted = stored,
                Rejected = rejected + accepted.Count - stored
            }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Matching generation failed for {Lang}/{Category}/{AnswerSource}",
                lang, categoryId, answerSource);
            return await FinishAsync(run with { Requested = wanted, Error = "generation_failed" }, ct);
        }
    }

    private async Task<MatchingGenerationRun> FinishAsync(MatchingGenerationRun run, CancellationToken ct)
    {
        var finished = run with { FinishedAt = clock.Now };
        await log.SaveAsync(finished, ct);
        return finished;
    }
}
