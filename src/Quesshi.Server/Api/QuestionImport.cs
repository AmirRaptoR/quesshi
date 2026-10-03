using System.Globalization;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>
/// Bulk-imports questions of one declared <see cref="QuestionKind"/> from a CSV or JSON file.
/// <para>
/// Every row is bound the same way the single-question form is: assembled into a
/// <see cref="SaveQuestionDto"/> and run through the existing <see cref="QuestionSaveBinding.TryBind"/>,
/// so a validation failure comes back with the same error code the form already uses. <c>subject</c>
/// and <c>aspect</c> are the one thing that doesn't fit on that DTO — they are read straight off the
/// row and turned into a <see cref="TopicKey"/>, the same split <c>TopUpQuestionBank</c> already makes
/// between "bind the shape" and "compute the topic".
/// </para>
/// <para>
/// A dry run and a commit run the identical checks, including duplicate-topic dedup against both the
/// rest of the file and what is already stored — which is why dedup is decided explicitly here rather
/// than left to the store's unique index: a dry run has nothing written yet to check that index
/// against.
/// </para>
/// </summary>
public static class QuestionImport
{
    public const long MaxImportBytes = 20 * 1024 * 1024;
    public const int MaxImportRows = 2000;

    private static readonly Dictionary<QuestionKind, string[]> KindFields = new()
    {
        [QuestionKind.Choice] = ["choice1", "choice2", "choice3", "choice4", "correctindex"],
        [QuestionKind.Sort] = ["item1", "item2", "item3", "item4"],
        [QuestionKind.Map] = ["targetshape", "countrycode", "latitude", "longitude", "radiuskm", "baselayer"]
    };

    private static IReadOnlyCollection<string> RequiredCsvColumns(QuestionKind kind)
        => ["lang", "categoryid", "level", "prompt", .. KindFields[kind]];

    /// <summary>
    /// Runs the import. A non-null <c>RequestError</c> means the whole request is refused before any
    /// row is read (bad kind, bad format, no file, a file too big, a whole JSON document that does not
    /// parse) — there is no per-row report for that case because there are no rows yet.
    /// </summary>
    public static Task<(string? RequestError, ImportReportDto? Report)> RunAsync(
        string? kind, string? format, Stream? content, long contentLength, bool dryRun,
        IQuestionRepository questions, IClock clock, IIdFactory ids, CancellationToken ct = default)
    {
        if (!TryParseKind(kind, out var parsedKind) || parsedKind == QuestionKind.Players)
            return Task.FromResult<(string?, ImportReportDto?)>(("bad_kind", null));

        return QuestionImportPipeline.RunAsync<Question>(format, content, contentLength, dryRun,
            RequiredCsvColumns(parsedKind),
            (fields, _) =>
            {
                var (question, prompt, error) = BindRow(fields, parsedKind, ids, clock.Now);
                QuestionImportPipeline.BoundRow<Question>? bound = question is null
                    ? null
                    : new(question, prompt, question.Lang, question.Topic);
                return Task.FromResult((bound, error));
            },
            (language, token) => questions.ExistingTopicsAsync(language, token),
            async (accepted, token) =>
            {
                await questions.UpsertManyAsync(accepted.Select(row => row.Bound.Value).ToList(), token);
                return new HashSet<int>();
            }, ct);
    }

    // --- per-row binding -------------------------------------------------------------

    private static (Question? Question, string? Prompt, string? Error) BindRow(
        IReadOnlyDictionary<string, string> f, QuestionKind kind, IIdFactory ids, DateTimeOffset now)
    {
        var prompt = Field(f, "prompt");

        var langRaw = Field(f, "lang")?.ToLowerInvariant();
        if (langRaw is not ("fa" or "en" or "nl")) return (null, prompt, "bad_lang");
        var lang = langRaw.ToLanguage();

        if (!int.TryParse(Field(f, "level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var levelInt)
            || !Enum.IsDefined((Difficulty)levelInt))
            return (null, prompt, "bad_level");

        var statusRaw = Field(f, "status");
        QuestionStatus status;
        if (statusRaw is null) status = QuestionStatus.Pending;
        else if (Enum.TryParse(statusRaw, true, out status) && Enum.IsDefined(status)) { }
        else return (null, prompt, "bad_status");

        var mediaUrl = Field(f, "mediaurl");
        MediaRef media;
        if (mediaUrl is null) media = MediaRef.None;
        else
        {
            var mediaKindRaw = Field(f, "mediakind");
            MediaKind mediaKind;
            if (mediaKindRaw is null) mediaKind = MediaKind.Image;
            else if (Enum.TryParse(mediaKindRaw, true, out mediaKind) && Enum.IsDefined(mediaKind)) { }
            else return (null, prompt, "bad_media_kind");
            media = new MediaRef(mediaKind, mediaUrl);
        }

        var (choices, correctIndex, target, baseLayer) = ShapeFor(kind, f);
        var categoryId = Field(f, "categoryid") ?? "";

        var saveDto = new SaveQuestionDto(null, langRaw, categoryId, levelInt, prompt ?? "",
            choices, correctIndex, Field(f, "explanation"), null, mediaUrl, statusRaw ?? "pending",
            kind.ToString().ToLowerInvariant(), target, baseLayer);

        if (QuestionSaveBinding.TryBind(saveDto, out var boundKind, out var mapTarget, out var mapBaseLayer) is { } bindError)
            return (null, prompt, bindError);

        var topic = TopicKey.From(Field(f, "subject"), Field(f, "aspect"));

        var question = Question.Create(ids.NewId(), lang, categoryId, (Difficulty)levelInt, prompt ?? "",
            choices, correctIndex, now, media, Field(f, "explanation"), QuestionSource.Admin, status,
            topic: topic, kind: boundKind, target: mapTarget, baseLayer: mapBaseLayer,
            knownCountryCodes: WorldMapCountries.Codes);

        return (question, prompt, null);
    }

    private static (List<string> Choices, int CorrectIndex, MapTargetDto? Target, string? BaseLayer) ShapeFor(
        QuestionKind kind, IReadOnlyDictionary<string, string> f) => kind switch
        {
            QuestionKind.Sort => ([Field(f, "item1") ?? "", Field(f, "item2") ?? "", Field(f, "item3") ?? "", Field(f, "item4") ?? ""],
                0, null, null),
            QuestionKind.Map => ([], 0, TargetFor(f), Field(f, "baselayer")),
            _ => ([Field(f, "choice1") ?? "", Field(f, "choice2") ?? "", Field(f, "choice3") ?? "", Field(f, "choice4") ?? ""],
                ParseCorrectIndex(f), null, null)
        };

    private static int ParseCorrectIndex(IReadOnlyDictionary<string, string> f)
        => int.TryParse(Field(f, "correctindex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : -1;

    private static MapTargetDto? TargetFor(IReadOnlyDictionary<string, string> f)
    {
        var shape = Field(f, "targetshape");
        if (shape is null) return null;

        return shape.ToLowerInvariant() switch
        {
            "country" => new MapTargetDto("country", Field(f, "countrycode")),
            "city" => new MapTargetDto("city", null, ParseDouble(f, "latitude"), ParseDouble(f, "longitude"), ParseDouble(f, "radiuskm")),
            _ => new MapTargetDto(shape)
        };
    }

    private static double? ParseDouble(IReadOnlyDictionary<string, string> f, string key)
        => double.TryParse(Field(f, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? Field(IReadOnlyDictionary<string, string> f, string key)
        => QuestionImportPipeline.Field(f, key);

    // --- request-level kind ------------------------------------------------------------

    /// <summary>
    /// Unlike <see cref="QuestionSaveBinding.TryParseKind"/>, which defaults a missing kind to
    /// <see cref="QuestionKind.Choice"/> for the single-question form's wire DTO, an import's kind is
    /// a required request parameter: there is no sensible default for "which template did you use".
    /// </summary>
    private static bool TryParseKind(string? value, out QuestionKind kind)
    {
        kind = QuestionKind.Choice;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out kind) && Enum.IsDefined(kind);
    }
}
