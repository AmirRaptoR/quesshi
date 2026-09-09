using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Shared;

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

    /// <summary>
    /// Every kind a bucket can hold, and therefore every kind a run tries to fill. Kind joins
    /// (language, category, level) as part of the bucket key because without it the bank's own
    /// inventory cannot see what it lacks: 3067 choice questions make every bucket look full, a
    /// top-up concludes there is nothing to do, and no sorting or map question is ever written.
    /// </summary>
    private static readonly QuestionKind[] Kinds = [QuestionKind.Choice, QuestionKind.Sort, QuestionKind.Map];

    /// <summary>
    /// How far outside its own country's outline a generated city may sit and still be believed.
    /// <para>
    /// The outlines come from the rendering asset — Natural Earth 1:110m, rounded to two decimals —
    /// so a genuinely correct coastal city can fall a few kilometres into the sea on it. This slack
    /// is chosen to be much larger than that artefact and much smaller than a country, because the
    /// error being hunted is "these coordinates are in Italy and the city is in Portugal", which is
    /// wrong by a thousand kilometres, not by twenty.
    /// </para>
    /// </summary>
    private const double CoordinateSlackKm = 30;

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
            .ToDictionary(b => (b.Lang, b.CategoryId, b.Level, b.Kind), b => b.Approved + b.Pending);

        // A category that already has questions is only topped up in the languages it already has
        // them in. Without this, adding a language means the next nightly run quietly writes a whole
        // new bank in it, one paid call at a time, for topics nobody asked for in that language.
        // A category with nothing at all anywhere is new, and gets filled in every language.
        //
        // This rule spans kinds on purpose, unlike the target below it. A category the app has Dutch
        // questions for is a Dutch category, whatever shape those questions are; asking "is this
        // category stocked in Dutch *sorting* questions?" would answer no for every category on
        // earth the first time this runs and so would never write the first one.
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
            foreach (var kind in Kinds)
            {
                if (inserted >= options.MaxPerRun) break;
                if (started.Contains(category.Id) && !stocked.Contains((lang, category.Id))) continue;

                // Each kind is measured against its own target. A bucket holding forty choice
                // questions and no sorts is a full choice bucket and an empty sorting one, and the
                // whole point of the kind being in the key is that those two facts stop cancelling
                // each other out.
                var have = counts.GetValueOrDefault((lang, category.Id, level, kind), 0);
                var want = Math.Min(options.TargetFor(kind) - have, options.BatchSize);
                if (want <= 0) continue;

                requested += want;

                try
                {
                    var result = await FillAsync(lang, category, level, want, index, kind, ct);
                    inserted += result.Inserted;
                    rejected += result.Rejected;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Generation failed for {Lang}/{Category}/{Level}/{Kind}", lang, category.Id, level, kind);
                    return await FinishAsync(run with { Requested = requested, Inserted = inserted, Rejected = rejected, Error = ex.Message }, ct);
                }
            }
        }

        return await FinishAsync(run with { Requested = requested, Inserted = inserted, Rejected = rejected }, ct);
    }

    /// <summary>
    /// One bucket, on demand — what the admin panel's generate button calls. The kind comes last and
    /// defaults to <see cref="QuestionKind.Choice"/> so the call this replaced still reads the same;
    /// the admin form passes it so an admin can ask for a batch of sorts or maps without waiting for
    /// a nightly run to notice the bucket is thin.
    /// </summary>
    public async Task<GenerationRun> GenerateOnceAsync(Language lang, string categoryId, Difficulty level, int count,
        QuestionKind kind = QuestionKind.Choice, CancellationToken ct = default)
    {
        var run = new GenerationRun(ids.NewId(), clock.Now, null, count, 0, 0, null);

        if (!generator.IsConfigured)
            return await FinishAsync(run with { Requested = 0, Error = "generator not configured" }, ct);

        if (await categories.GetAsync(categoryId, ct) is not { } category)
            return await FinishAsync(run with { Requested = 0, Error = $"no such category: {categoryId}" }, ct);

        var wanted = Math.Clamp(count, 1, options.MaxPerRun);

        try
        {
            var result = await FillAsync(lang, category, level, wanted, await IndexOfAsync(categoryId, ct), kind, ct);
            return await FinishAsync(run with { Requested = wanted, Inserted = result.Inserted, Rejected = result.Rejected }, ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Generation failed for {Lang}/{Category}/{Level}/{Kind}", lang, categoryId, level, kind);
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

                index.Add(question.Prompt, AnswerOf(question));
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
        int want, PromptIndex index, QuestionKind kind, CancellationToken ct)
    {
        var recent = index.Recent(60);

        // One prompt and one response schema per kind, all landing in the same acceptance path
        // below — so de-duplication, the topic index and the review setting work identically for a
        // sort and a map as they always have for a choice question.
        var batch = kind switch
        {
            QuestionKind.Sort => await generator.GenerateSortAsync(lang, category, level, want, recent, ct),
            QuestionKind.Map => await generator.GenerateMapAsync(lang, category, level, want, recent, ct),
            _ => await generator.GenerateAsync(lang, category, level, want, recent, ct)
        };

        var accepted = new List<Question>();
        var rejected = 0;

        foreach (var candidate in batch)
        {
            if (!TryAccept(candidate, lang, category.Id, level, index, out var question, kind))
            {
                rejected++;
                continue;
            }

            index.Add(question!.Prompt, AnswerOf(question));
            accepted.Add(question);
        }


        // The store rejects anything whose topic already exists in this language, so what landed is
        // the only number worth reporting.
        var stored = accepted.Count > 0 ? await questions.UpsertManyAsync(accepted, ct) : 0;

        return (stored, rejected + (accepted.Count - stored));
    }

    /// <summary>Trust boundary. Everything here came out of a language model.</summary>
    private bool TryAccept(GeneratedQuestion candidate, Language lang, string categoryId, Difficulty level,
        PromptIndex index, out Question? question, QuestionKind kind = QuestionKind.Choice)
    {
        question = null;

        var prompt = candidate.Prompt?.Trim() ?? string.Empty;

        var answer = candidate.CorrectIndex >= 0 && candidate.CorrectIndex < (candidate.Choices?.Count ?? 0)
            ? candidate.Choices![candidate.CorrectIndex]
            : null;

        if (index.Contains(prompt, answer)) return false;

        MapTarget? target = null;
        if (kind == QuestionKind.Map && !TryMapTarget(candidate, prompt, out target)) return false;

        try
        {
            // The kind is the one the pipeline asked for, never one the model claims: a batch was
            // requested with a sorting prompt and a sorting schema, so a sorting question is what
            // comes back or nothing does. knownCountryCodes is what makes "a country the map can
            // actually draw" a rule rather than a hope — it is the same set the play screen paints.
            question = Question.Create(ids.NewId(), lang, categoryId, level, prompt,
                kind == QuestionKind.Map ? [] : candidate.Choices,
                kind == QuestionKind.Choice ? candidate.CorrectIndex : 0,
                clock.Now, explanation: candidate.Explanation, source: QuestionSource.Ai,
                status: options.AutoApprove ? QuestionStatus.Approved : QuestionStatus.Pending,
                topic: TopicKey.From(candidate.Subject, candidate.Aspect),
                kind: kind, target: target,
                baseLayer: kind == QuestionKind.Map ? GeneratedBaseLayer : null,
                knownCountryCodes: WorldMapCountries.Codes);
            return true;
        }
        catch (ArgumentException ex)
        {
            logger?.LogInformation("Rejecting a generated {Kind} question: {Reason}", kind, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Which map a generated question is played on. Borders, always — a blank map is the harder
    /// layer because the player has to know where the boundaries are, and nobody has looked at a
    /// freshly generated question yet. An admin who wants a harder version changes it in the form,
    /// which is the right place for a judgement call about difficulty.
    /// </summary>
    private const MapBaseLayer GeneratedBaseLayer = MapBaseLayer.Borders;

    /// <summary>
    /// Turns a map candidate's answer into a target, or refuses it with a logged reason.
    ///
    /// <para>
    /// The country case is the cheap one: a code the bundled SVG has no path for is a question the
    /// map could never highlight, so it is refused here as well as by <c>Question.Validate</c> — here
    /// because this is where a reason can be written down.
    /// </para>
    /// <para>
    /// The city case is the one this method exists for. A model will happily name a real city, name
    /// the right country, and give coordinates a thousand kilometres away in a neighbouring one;
    /// nothing about the strings betrays it, and the question then plays as a place that is simply
    /// not where the game says it is. Asking for the country as well turns that into something
    /// checkable: the coordinates either fall inside the country's own outline on the very map the
    /// player will tap, or the candidate is dropped. The country itself is then thrown away — it was
    /// never part of the question, only of the proof.
    /// </para>
    /// </summary>
    private bool TryMapTarget(GeneratedQuestion candidate, string prompt, out MapTarget? target)
    {
        target = null;

        var code = candidate.CountryCode?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(code) || !WorldMapCountries.Codes.Contains(code))
        {
            logger?.LogInformation("Rejecting map question '{Prompt}': '{Code}' is not a country the map can draw.", prompt, code);
            return false;
        }

        if (candidate.TargetShape != MapTargetKind.City)
        {
            target = MapTarget.Country(code);
            return true;
        }

        if (candidate.Latitude is not { } latitude || candidate.Longitude is not { } longitude)
        {
            logger?.LogInformation("Rejecting map question '{Prompt}': a city target with no coordinates.", prompt);
            return false;
        }

        if (!WorldMapGeometry.Contains(code, latitude, longitude, CoordinateSlackKm))
        {
            logger?.LogInformation(
                "Rejecting map question '{Prompt}': {Latitude},{Longitude} is not inside {Code}.",
                prompt, latitude, longitude, code);
            return false;
        }

        try
        {
            // The radius is a difficulty lever rather than a fact about the world, so a model that
            // proposes an unplayable 5 km has not lied — it has picked a number outside what the
            // game can draw, and the nearest playable one keeps an otherwise good question. A
            // non-finite radius is not a number at all and still throws its way out below.
            target = MapTarget.City(latitude, longitude,
                Math.Clamp(candidate.RadiusKm ?? DefaultRadiusKm, MapTarget.MinRadiusKm, MapTarget.MaxRadiusKm));
            return true;
        }
        catch (ArgumentException ex)
        {
            logger?.LogInformation("Rejecting map question '{Prompt}': {Reason}", prompt, ex.Message);
            return false;
        }
    }

    /// <summary>Where a candidate names no radius: gentle enough that a player who knows the city
    /// but not the pixel is right, tight enough that the neighbouring country is not.</summary>
    private const double DefaultRadiusKm = 250;

    /// <summary>
    /// What the de-duplication index files a question's answer under. A choice question's answer is
    /// the chosen option; a map question's is its target, spelled exactly as an answer would be; a
    /// sorting question has no single answer to name — its answer is the order — so it is indexed on
    /// wording and topic alone, which is what <c>PromptIndex</c> does with a null.
    /// </summary>
    private static string? AnswerOf(Question question) => question.Kind switch
    {
        QuestionKind.Map => question.Target?.ToResponse(),
        QuestionKind.Sort => null,
        _ => question.Choices[question.CorrectIndex]
    };

    private async Task<GenerationRun> FinishAsync(GenerationRun run, CancellationToken ct)
    {
        var finished = run with { FinishedAt = clock.Now };
        await log.SaveAsync(finished, ct);
        return finished;
    }
}
