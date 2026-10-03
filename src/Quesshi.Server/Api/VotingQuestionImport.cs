using System.Globalization;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>
/// Bulk-imports voting questions. This is deliberately a separate importer from
/// <see cref="QuestionImport": voting questions have no level, correct answer, or trivia kind.
/// </summary>
public static class VotingQuestionImport
{
    private static readonly string[] RequiredColumns = ["lang", "categoryid", "prompt", "answersource"];
    private static readonly string[] ChoiceColumns = ["choice1", "choice2", "choice3", "choice4",
        "choice5", "choice6", "choice7", "choice8"];

    public static Task<(string? RequestError, ImportReportDto? Report)> RunAsync(
        string? format, Stream? content, long contentLength, bool dryRun,
        IVotingQuestionRepository questions, ICategoryRepository categories,
        IClock clock, IIdFactory ids, CancellationToken ct = default)
        => QuestionImportPipeline.RunAsync<VotingQuestion>(format, content, contentLength, dryRun,
            RequiredColumns,
            async (fields, token) =>
            {
                var (question, error) = await BindRowAsync(fields, categories, ids, clock.Now, token);
                return (question is null ? null : new QuestionImportPipeline.BoundRow<VotingQuestion>(
                    question, question.Prompt, question.Lang, question.Topic), error);
            },
            (language, token) => questions.ExistingTopicsAsync(language, token),
            async (accepted, token) =>
            {
                var rejected = new HashSet<int>();
                foreach (var candidate in accepted)
                {
                    try { await questions.UpsertAsync(candidate.Bound.Value, token); }
                    catch (InvalidOperationException) { rejected.Add(candidate.Row); }
                    catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000) { rejected.Add(candidate.Row); }
                }
                return rejected;
            }, ct);

    private static async Task<(VotingQuestion? Question, string? Error)> BindRowAsync(
        IReadOnlyDictionary<string, string> fields, ICategoryRepository categories,
        IIdFactory ids, DateTimeOffset now, CancellationToken ct)
    {
        var langRaw = Field(fields, "lang")?.ToLowerInvariant();
        if (langRaw is not ("fa" or "en" or "nl")) return (null, "bad_lang");
        var lang = langRaw.ToLanguage();

        var categoryId = Field(fields, "categoryid")?.ToLowerInvariant();
        if (categoryId is null) return (null, "unknown_category");
        var category = await categories.GetAsync(categoryId, ct);
        if (category is null) return (null, "unknown_category");
        if (!category.IsActive) return (null, "inactive_category");

        var prompt = Field(fields, "prompt");
        if (prompt is null) return (null, "blank_prompt");

        var sourceRaw = Field(fields, "answersource")?.ToLowerInvariant();
        if (sourceRaw is not ("participants" or "fixed")) return (null, "bad_answer_source");
        var source = sourceRaw == "fixed" ? VotingAnswerSource.Fixed : VotingAnswerSource.Participants;
        var choices = ChoiceColumns.Select(key => Field(fields, key)).Where(value => value is not null)
            .Select(value => value!).ToList();

        if (source == VotingAnswerSource.Participants && choices.Count != 0)
            return (null, "choices_not_allowed");
        if (source == VotingAnswerSource.Fixed && choices.Count < VotingRules.MinFixedChoices)
            return (null, "too_few_choices");
        if (source == VotingAnswerSource.Fixed && choices.Count > VotingRules.MaxFixedChoices)
            return (null, "too_many_choices");
        if (choices.Select(c => c.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count() != choices.Count)
            return (null, "duplicate_choice");

        var media = MediaRef.None;
        var mediaUrl = fields.TryGetValue("mediaurl", out var rawUrl) ? rawUrl : null;
        var mediaKind = fields.TryGetValue("mediakind", out var rawKind) ? rawKind : null;
        if (mediaUrl is null || mediaUrl.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(mediaKind)) return (null, "bad_media");
        }
        else if (string.IsNullOrWhiteSpace(mediaUrl)) return (null, "bad_media");
        else if (!Enum.TryParse<MediaKind>(mediaKind?.Trim(), true, out var parsedMedia)
            || !Enum.IsDefined(parsedMedia) || parsedMedia == MediaKind.None)
            return (null, "bad_media");
        else media = new MediaRef(parsedMedia, mediaUrl.Trim());

        var statusRaw = Field(fields, "status");
        var status = QuestionStatus.Pending;
        if (statusRaw is not null && (!Enum.TryParse(statusRaw, true, out status) || !Enum.IsDefined(status)))
            return (null, "bad_status");

        var topic = TopicKey.From(Field(fields, "subject"), Field(fields, "aspect"));
        var question = VotingQuestion.Create(ids.NewId(), lang, category.Id, prompt, source, choices, now,
            media, QuestionSource.Admin, status, topic);
        return (question, null);
    }

    private static string? Field(IReadOnlyDictionary<string, string> fields, string key)
        => QuestionImportPipeline.Field(fields, key);

}
