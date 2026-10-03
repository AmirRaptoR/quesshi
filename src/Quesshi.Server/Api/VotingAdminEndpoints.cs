using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Admin authoring endpoints for the voting bounded context. These use voting stores and
/// contracts throughout; trivia routes remain in <see cref="AdminEndpoints"/>.</summary>
public static class VotingAdminEndpoints
{
    public static void MapVotingAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/voting/questions", async (string? lang, string? category, string? status, string? text,
            int? skip, int? take, IVotingQuestionRepository questions) =>
        {
            var filter = new VotingQuestionFilter(
                Lang: string.IsNullOrWhiteSpace(lang) ? null : lang.ToLanguage(),
                CategoryId: string.IsNullOrWhiteSpace(category) ? null : category,
                Status: ParseStatus(status),
                Text: string.IsNullOrWhiteSpace(text) ? null : text,
                Skip: Math.Max(0, skip ?? 0),
                Take: Math.Clamp(take ?? 25, 1, 100));

            return new AdminVotingQuestionPageDto(
                [.. (await questions.FindAsync(filter)).Select(q => q.ToAdminDto())],
                await questions.CountAsync(filter));
        });

        // Voting imports stay in this bounded context. In particular, do not add these fields to
        // the trivia importer: voting has no level or correct answer and its choices come from a
        // declared answer source.
        admin.MapPost("/voting/questions/import", async (IFormFile? file, string? format,
            IVotingQuestionRepository questions, ICategoryRepository categories,
            IClock clock, IIdFactory ids, bool dryRun = true) =>
        {
            await using var stream = file?.OpenReadStream();
            var (error, report) = await VotingQuestionImport.RunAsync(format, stream, file?.Length ?? 0,
                dryRun, questions, categories, clock, ids);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(report);
        }).DisableAntiforgery();

        admin.MapGet("/voting/questions/import/template", (string? format) =>
        {
            var (error, template) = VotingQuestionImportTemplates.Build(format);
            if (error is not null) return Results.BadRequest(new { error });
            return Results.File(template!.Content, template.ContentType, template.FileName);
        });

        admin.MapPost("/voting/generate", async (GenerateVotingRequestDto body,
            GenerateVotingQuestions generation) =>
        {
            var langValue = body.Lang?.Trim().ToLowerInvariant();
            if (langValue is not ("fa" or "en" or "nl"))
                return Results.BadRequest(new { error = "bad_lang" });

            var sourceValue = body.AnswerSource?.Trim().ToLowerInvariant();
            if (sourceValue is not ("participants" or "fixed"))
                return Results.BadRequest(new { error = "bad_answer_source" });

            if (body.Count is < 1 or > 20)
                return Results.BadRequest(new { error = "bad_count" });

            var answerSource = sourceValue == "fixed"
                ? VotingAnswerSource.Fixed
                : VotingAnswerSource.Participants;
            var run = await generation.RunAsync(langValue.ToLanguage(),
                NormaliseCategoryReference(body.CategoryId), answerSource, body.Count);
            if (run.Error is "unknown_category" or "inactive_category")
                return Results.BadRequest(new { error = run.Error });
            return Results.Ok(run.ToDto());
        });

        admin.MapPost("/voting/questions", async (SaveVotingQuestionDto body,
            IVotingQuestionRepository questions, ICategoryRepository categories,
            IClock clock, IIdFactory ids) =>
        {
            var error = VotingQuestionSaveBinding.TryBind(body, out var lang, out var answerSource,
                out var choices, out var media, out var status, out var topic);
            if (error is not null) return Results.BadRequest(new { error });

            var suppliedId = string.IsNullOrWhiteSpace(body.Id) ? null : body.Id!.Trim();
            var existing = suppliedId is null ? null : await questions.GetAsync(suppliedId);
            if (suppliedId is not null && existing is null) return Results.NotFound();

            var categoryId = string.IsNullOrWhiteSpace(body.CategoryId) ? null : NormaliseCategoryReference(body.CategoryId);
            var category = categoryId is null ? null : await categories.GetAsync(categoryId);
            if (categoryId is not null && category is null) return Results.BadRequest(new { error = "unknown_category" });

            // Existing content under a category remains editable after that category is retired;
            // moving new content into the retired category is refused.
            if (category is { IsActive: false } && (existing is null || existing.CategoryId != category.Id))
                return Results.BadRequest(new { error = "inactive_category" });

            if (topic is not null)
            {
                var topics = await questions.ExistingTopicsAsync(lang);
                var sameExistingTopic = existing is not null && existing.Lang == lang && existing.Topic == topic;
                if (topics.Contains(topic) && !sameExistingTopic)
                    return Results.BadRequest(new { error = "duplicate_topic" });
            }

            try
            {
                if (existing is not null)
                {
                    existing.Edit(lang, category?.Id, body.Prompt, answerSource, choices, media, topic, clock.Now);
                    existing.SetStatus(status);
                    await questions.UpsertAsync(existing);
                    return Results.Ok(existing.ToAdminDto());
                }

                var created = VotingQuestion.Create(ids.NewId(), lang, category?.Id, body.Prompt,
                    answerSource, choices, clock.Now, media, QuestionSource.Admin, status, topic);
                await questions.UpsertAsync(created);
                return Results.Ok(created.ToAdminDto());
            }
            catch (ArgumentException ex)
            {
                // The domain is the final authority. A future validation rule must still leave the
                // HTTP contract as a translatable 400 rather than leaking a 500.
                return Results.BadRequest(new { error = DomainErrorCode(ex) });
            }
            catch (InvalidOperationException)
            {
                // The in-memory test store and some deployments surface the unique topic race this
                // way; Mongo is handled below. The only expected operation-level rejection here is
                // the topic's unique key.
                return Results.BadRequest(new { error = "duplicate_topic" });
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
            {
                return Results.BadRequest(new { error = "duplicate_topic" });
            }
        });

        admin.MapPost("/voting/questions/{id}/approve", (string id, IVotingQuestionRepository questions)
            => SetStatusAsync(id, questions, approve: true));
        admin.MapPost("/voting/questions/{id}/reject", (string id, IVotingQuestionRepository questions)
            => SetStatusAsync(id, questions, approve: false));

        admin.MapDelete("/voting/questions/{id}", async (string id, IVotingQuestionRepository questions) =>
        {
            if (await questions.GetAsync(id) is null) return Results.NotFound();
            await questions.DeleteAsync(id);
            return Results.Ok();
        });

    }

    private static async Task<IResult> SetStatusAsync(string id, IVotingQuestionRepository questions, bool approve)
    {
        if (await questions.GetAsync(id) is not { } question) return Results.NotFound();
        question.SetStatus(approve ? QuestionStatus.Approved : QuestionStatus.Rejected);
        await questions.UpsertAsync(question);
        return Results.Ok(question.ToAdminDto());
    }

    private static QuestionStatus? ParseStatus(string? value)
        => Enum.TryParse<QuestionStatus>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static string NormaliseCategoryReference(string? value)
        => value?.Trim().ToLowerInvariant() ?? "";

    private static string DomainErrorCode(ArgumentException ex)
        => ex.ParamName switch
        {
            "prompt" => "blank_prompt",
            "choices" => "bad_choices",
            "answerSource" => "bad_answer_source",
            _ => "bad_voting_question"
        };

}
