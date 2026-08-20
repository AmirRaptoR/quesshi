using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>
/// Asks the model for questions and keeps only what survives validation and de-duplication.
/// By default what survives is published immediately and policed afterwards by player reports;
/// see <see cref="TopUpOptions.AutoApprove"/>.
/// </summary>
public sealed class TopUpQuestionBank(
    IQuestionRepository questions,
    ICategoryRepository categories,
    IQuestionGenerator generator,
    IGenerationLog log,
    IClock clock,
    IIdFactory ids,
    TopUpOptions options,
    IQuestionImageProvider? images = null,
    ILogger<TopUpQuestionBank>? logger = null)
{
    private static readonly Language[] Languages = [Language.Fa, Language.En, Language.Nl];

    /// <summary>The scheduled job: find every thin bucket and fill it.</summary>
    public async Task<GenerationRun> RunAsync(CancellationToken ct = default)
    {
        var run = new GenerationRun(ids.NewId(), clock.Now, null, 0, 0, 0, null);

        if (!generator.IsConfigured)
        {
            logger?.LogInformation("Question generator is not configured; skipping top-up.");
            return await FinishAsync(run with { Error = "generator not configured" }, ct);
        }

        var active = (await categories.AllAsync(ct)).Where(c => c.IsActive).ToList();
        var counts = (await questions.BucketCountsAsync(ct))
            .ToDictionary(b => (b.Lang, b.CategoryId, b.Level), b => b.Approved + b.Pending);

        // A category that already has questions is only topped up in the languages it already has
        // them in. Without this, adding a language means the next nightly run quietly writes a whole
        // new bank in it, one paid call at a time, for topics nobody asked for in that language.
        // A category with nothing at all anywhere is new, and gets filled in every language.
        var stocked = counts.Where(c => c.Value > 0)
            .Select(c => (c.Key.Lang, c.Key.CategoryId))
            .ToHashSet();
        var started = stocked.Select(pair => pair.CategoryId).ToHashSet();

        int requested = 0, inserted = 0, rejected = 0;

        foreach (var category in active)
        {
            var index = await IndexOfAsync(category.Id, ct);

            foreach (var lang in Languages)
            foreach (var level in MatchRules.AllLevels)
            {
                if (inserted >= options.MaxPerRun) break;
                if (started.Contains(category.Id) && !stocked.Contains((lang, category.Id))) continue;

                var have = counts.GetValueOrDefault((lang, category.Id, level), 0);
                var want = Math.Min(options.TargetPerBucket - have, options.BatchSize);
                if (want <= 0) continue;

                requested += want;

                try
                {
                    var result = await FillAsync(lang, category, level, want, index, ct);
                    inserted += result.Inserted;
                    rejected += result.Rejected;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Generation failed for {Lang}/{Category}/{Level}", lang, category.Id, level);
                    return await FinishAsync(run with { Requested = requested, Inserted = inserted, Rejected = rejected, Error = ex.Message }, ct);
                }
            }
        }

        return await FinishAsync(run with { Requested = requested, Inserted = inserted, Rejected = rejected }, ct);
    }

    /// <summary>One bucket, on demand — what the admin panel's generate button calls.</summary>
    public async Task<GenerationRun> GenerateOnceAsync(Language lang, string categoryId, Difficulty level, int count, CancellationToken ct = default)
    {
        var run = new GenerationRun(ids.NewId(), clock.Now, null, count, 0, 0, null);

        if (!generator.IsConfigured)
            return await FinishAsync(run with { Requested = 0, Error = "generator not configured" }, ct);

        if (await categories.GetAsync(categoryId, ct) is not { } category)
            return await FinishAsync(run with { Requested = 0, Error = $"no such category: {categoryId}" }, ct);

        var wanted = Math.Clamp(count, 1, options.MaxPerRun);

        try
        {
            var result = await FillAsync(lang, category, level, wanted, await IndexOfAsync(categoryId, ct), ct);
            return await FinishAsync(run with { Requested = wanted, Inserted = result.Inserted, Rejected = result.Rejected }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Generation failed for {Lang}/{Category}/{Level}", lang, categoryId, level);
            return await FinishAsync(run with { Requested = wanted, Error = ex.Message }, ct);
        }
    }

    /// <summary>
    /// Picture questions, on demand. Each candidate names a subject; if no freely-licensed picture
    /// of it can be found the candidate is dropped, because a picture question without a picture
    /// is not a question.
    /// </summary>
    public async Task<GenerationRun> GenerateIllustratedAsync(Language lang, string categoryId, Difficulty level, int count, CancellationToken ct = default)
    {
        var run = new GenerationRun(ids.NewId(), clock.Now, null, count, 0, 0, null);

        if (!generator.IsConfigured)
            return await FinishAsync(run with { Requested = 0, Error = "generator not configured" }, ct);

        if (images?.IsConfigured != true)
            return await FinishAsync(run with { Requested = 0, Error = "no image source configured" }, ct);

        if (await categories.GetAsync(categoryId, ct) is not { } category)
            return await FinishAsync(run with { Requested = 0, Error = $"no such category: {categoryId}" }, ct);

        var wanted = Math.Clamp(count, 1, options.MaxPerRun);
        var index = await IndexOfAsync(categoryId, ct);

        try
        {
            var batch = await generator.GenerateIllustratedAsync(lang, category, level, wanted, index.Recent(40), ct);

            var accepted = new List<Question>();
            var rejected = 0;

            foreach (var candidate in batch)
            {
                if (!TryAccept(candidate, lang, categoryId, level, index, out var question))
                {
                    rejected++;
                    continue;
                }

                var media = await images.ProvideAsync(candidate.Subject ?? "", ct);
                if (media is null)
                {
                    logger?.LogInformation("No usable picture for {Subject}; dropping the question", candidate.Subject);
                    rejected++;
                    continue;
                }

                question!.Edit(lang, categoryId, level, question.Prompt, question.Choices, question.CorrectIndex, media, question.Explanation);

                index.Add(question.Prompt, question.Choices[question.CorrectIndex]);
                accepted.Add(question);
            }

            var stored = accepted.Count > 0 ? await questions.UpsertManyAsync(accepted, ct) : 0;

            return await FinishAsync(run with { Requested = wanted, Inserted = stored, Rejected = rejected + (accepted.Count - stored) }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Illustrated generation failed for {Lang}/{Category}/{Level}", lang, categoryId, level);
            return await FinishAsync(run with { Requested = wanted, Error = ex.Message }, ct);
        }
    }

    private async Task<PromptIndex> IndexOfAsync(string categoryId, CancellationToken ct)
        => new((await questions.ExistingQuestionsAsync(categoryId, ct)).Select(q => (q.Prompt, (string?)q.Answer)));

    /// <summary>Generate, validate, de-duplicate, store. The index grows as we accept, so one batch cannot repeat itself.</summary>
    private async Task<(int Inserted, int Rejected)> FillAsync(Language lang, Category category, Difficulty level,
        int want, PromptIndex index, CancellationToken ct)
    {
        var batch = await generator.GenerateAsync(lang, category, level, want, index.Recent(60), ct);

        var accepted = new List<Question>();
        var rejected = 0;

        foreach (var candidate in batch)
        {
            if (!TryAccept(candidate, lang, category.Id, level, index, out var question))
            {
                rejected++;
                continue;
            }

            index.Add(question!.Prompt, question.Choices[question.CorrectIndex]);
            accepted.Add(question);
        }


        // The store rejects anything whose topic already exists in this language, so what landed is
        // the only number worth reporting.
        var stored = accepted.Count > 0 ? await questions.UpsertManyAsync(accepted, ct) : 0;

        return (stored, rejected + (accepted.Count - stored));
    }

    /// <summary>Trust boundary. Everything here came out of a language model.</summary>
    private bool TryAccept(GeneratedQuestion candidate, Language lang, string categoryId, Difficulty level,
        PromptIndex index, out Question? question)
    {
        question = null;

        var prompt = candidate.Prompt?.Trim() ?? string.Empty;

        var answer = candidate.CorrectIndex >= 0 && candidate.CorrectIndex < (candidate.Choices?.Count ?? 0)
            ? candidate.Choices![candidate.CorrectIndex]
            : null;

        if (index.Contains(prompt, answer)) return false;

        try
        {
            question = Question.Create(ids.NewId(), lang, categoryId, level, prompt, candidate.Choices, candidate.CorrectIndex,
                clock.Now, explanation: candidate.Explanation, source: QuestionSource.Ai,
                status: options.AutoApprove ? QuestionStatus.Approved : QuestionStatus.Pending,
                topic: TopicKey.From(candidate.Subject, candidate.Aspect));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<GenerationRun> FinishAsync(GenerationRun run, CancellationToken ct)
    {
        var finished = run with { FinishedAt = clock.Now };
        await log.SaveAsync(finished, ct);
        return finished;
    }
}
