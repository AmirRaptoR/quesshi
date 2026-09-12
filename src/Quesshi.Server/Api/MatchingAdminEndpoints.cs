using System.Text.RegularExpressions;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Admin authoring endpoints for the matching bounded context. These use matching stores and
/// contracts throughout; trivia routes remain in <see cref="AdminEndpoints"/>.</summary>
public static class MatchingAdminEndpoints
{
    private static readonly Regex CategoryId = new("^[a-z0-9]+(?:[-_][a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{3}(?:[0-9a-fA-F]{3})?$", RegexOptions.Compiled);

    public static void MapMatchingAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/matching/questions", async (string? lang, string? category, string? status, string? text,
            int? skip, int? take, IMatchingQuestionRepository questions) =>
        {
            var filter = new MatchingQuestionFilter(
                Lang: string.IsNullOrWhiteSpace(lang) ? null : lang.ToLanguage(),
                CategoryId: string.IsNullOrWhiteSpace(category) ? null : category,
                Status: ParseStatus(status),
                Text: string.IsNullOrWhiteSpace(text) ? null : text,
                Skip: Math.Max(0, skip ?? 0),
                Take: Math.Clamp(take ?? 25, 1, 100));

            return new AdminMatchingQuestionPageDto(
                [.. (await questions.FindAsync(filter)).Select(q => q.ToAdminDto())],
                await questions.CountAsync(filter));
        });

        admin.MapPost("/matching/questions", async (SaveMatchingQuestionDto body,
            IMatchingQuestionRepository questions, IMatchingCategoryRepository categories,
            IClock clock, IIdFactory ids) =>
        {
            var error = MatchingQuestionSaveBinding.TryBind(body, out var lang, out var answerSource,
                out var choices, out var media, out var status, out var topic);
            if (error is not null) return Results.BadRequest(new { error });

            var suppliedId = string.IsNullOrWhiteSpace(body.Id) ? null : body.Id!.Trim();
            var existing = suppliedId is null ? null : await questions.GetAsync(suppliedId);
            if (suppliedId is not null && existing is null) return Results.NotFound();

            var categoryId = NormaliseCategoryReference(body.MatchingCategoryId);
            var category = await categories.GetAsync(categoryId);
            if (category is null) return Results.BadRequest(new { error = "unknown_category" });

            // Existing content under a category remains editable after that category is retired;
            // moving new content into the retired category is refused.
            if (!category.IsActive && (existing is null || existing.MatchingCategoryId != category.Id))
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
                    existing.Edit(lang, category.Id, body.Prompt, answerSource, choices, media, topic, clock.Now);
                    existing.SetStatus(status);
                    await questions.UpsertAsync(existing);
                    return Results.Ok(existing.ToAdminDto());
                }

                var created = MatchingQuestion.Create(ids.NewId(), lang, category.Id, body.Prompt,
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

        admin.MapPost("/matching/questions/{id}/approve", (string id, IMatchingQuestionRepository questions)
            => SetStatusAsync(id, questions, approve: true));
        admin.MapPost("/matching/questions/{id}/reject", (string id, IMatchingQuestionRepository questions)
            => SetStatusAsync(id, questions, approve: false));

        admin.MapDelete("/matching/questions/{id}", async (string id, IMatchingQuestionRepository questions) =>
        {
            if (await questions.GetAsync(id) is null) return Results.NotFound();
            await questions.DeleteAsync(id);
            return Results.Ok();
        });

        admin.MapGet("/matching/categories", async (IMatchingCategoryRepository categories) =>
            (await categories.AllAsync()).Select(c => c.ToDto(Language.Fa)).ToList());

        admin.MapPost("/matching/categories", async (MatchingCategoryDto body,
            IMatchingCategoryRepository categories) =>
        {
            var nameFa = (body.NameFa ?? "").Trim();
            var nameEn = (body.NameEn ?? "").Trim();
            var nameNl = (body.NameNl ?? "").Trim();
            if (nameFa.Length == 0 && nameEn.Length == 0)
                return Results.BadRequest(new { error = "blank_name" });

            var (iconFa, cleanFa) = SplitIcon(nameFa);
            var (iconEn, cleanEn) = SplitIcon(nameEn);
            var (iconNl, cleanNl) = SplitIcon(nameNl);
            nameFa = cleanFa;
            nameEn = cleanEn;
            nameNl = cleanNl;
            if (nameFa.Length == 0 && nameEn.Length == 0)
                return Results.BadRequest(new { error = "blank_name" });

            var rawId = (body.Id ?? "").Trim().ToLowerInvariant();
            if (rawId.Length == 0) return Results.BadRequest(new { error = "bad_category_id" });
            var id = rawId.StartsWith("m-", StringComparison.Ordinal) ? rawId : "m-" + rawId;
            if (!CategoryId.IsMatch(id)) return Results.BadRequest(new { error = "bad_category_id" });

            var color = (body.Color ?? "").Trim();
            if (!HexColor.IsMatch(color))
                return Results.BadRequest(new { error = "bad_color" });

            var existing = await categories.GetAsync(id);
            var all = await categories.AllAsync();
            var icon = string.IsNullOrWhiteSpace(body.Icon)
                ? new[] { iconFa, iconEn, iconNl }.FirstOrDefault(i => i.Length > 0) ?? "◆"
                : body.Icon.Trim();
            var order = body.SortOrder > 0
                ? body.SortOrder
                : existing?.SortOrder ?? all.Select(c => c.SortOrder).DefaultIfEmpty(0).Max() + 1;

            await categories.UpsertAsync(new MatchingCategory(id, nameFa, nameEn, icon,
                color, body.IsActive, order, nameNl));
            return Results.Ok((await categories.GetAsync(id))!.ToDto(Language.Fa));
        });

        admin.MapDelete("/matching/categories/{id}", async (string id,
            IMatchingCategoryRepository categories, IMatchingQuestionRepository questions) =>
        {
            var categoryId = NormaliseCategoryReference(id);
            if (await questions.CountAsync(new MatchingQuestionFilter(CategoryId: categoryId)) > 0)
                return Results.BadRequest(new { error = "category_in_use" });

            if (await categories.GetAsync(categoryId) is null) return Results.NotFound();
            await categories.DeleteAsync(categoryId);
            return Results.Ok();
        });
    }

    private static async Task<IResult> SetStatusAsync(string id, IMatchingQuestionRepository questions, bool approve)
    {
        if (await questions.GetAsync(id) is not { } question) return Results.NotFound();
        question.SetStatus(approve ? QuestionStatus.Approved : QuestionStatus.Rejected);
        await questions.UpsertAsync(question);
        return Results.Ok(question.ToAdminDto());
    }

    private static QuestionStatus? ParseStatus(string? value)
        => Enum.TryParse<QuestionStatus>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static string NormaliseCategoryReference(string? value)
    {
        var raw = value?.Trim().ToLowerInvariant() ?? "";
        return raw.StartsWith("m-", StringComparison.Ordinal) ? raw : "m-" + raw;
    }

    private static string DomainErrorCode(ArgumentException ex)
        => ex.ParamName switch
        {
            "prompt" => "blank_prompt",
            "choices" => "bad_choices",
            "answerSource" => "bad_answer_source",
            _ => "bad_matching_question"
        };

    private static (string Icon, string Name) SplitIcon(string value)
    {
        if (value.Length == 0) return ("", "");
        if (char.IsLetterOrDigit(value, 0) || char.IsPunctuation(value, 0)) return ("", value);
        var length = System.Globalization.StringInfo.GetNextTextElementLength(value);
        return (value[..length], value[length..].Trim());
    }
}
