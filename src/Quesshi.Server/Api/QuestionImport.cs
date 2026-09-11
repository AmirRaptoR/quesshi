using System.Globalization;
using System.Text;
using System.Text.Json;
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

    private static readonly string[] CommonFields = ["lang", "categoryid", "level", "prompt", "explanation",
        "mediaurl", "mediakind", "status", "subject", "aspect"];

    private static readonly Dictionary<QuestionKind, string[]> KindFields = new()
    {
        [QuestionKind.Choice] = ["choice1", "choice2", "choice3", "choice4", "correctindex"],
        [QuestionKind.Sort] = ["item1", "item2", "item3", "item4"],
        [QuestionKind.Map] = ["targetshape", "countrycode", "latitude", "longitude", "radiuskm", "baselayer"]
    };

    /// <summary>
    /// Runs the import. A non-null <c>RequestError</c> means the whole request is refused before any
    /// row is read (bad kind, bad format, no file, a file too big, a whole JSON document that does not
    /// parse) — there is no per-row report for that case because there are no rows yet.
    /// </summary>
    public static async Task<(string? RequestError, ImportReportDto? Report)> RunAsync(
        string? kind, string? format, Stream? content, long contentLength, bool dryRun,
        IQuestionRepository questions, IClock clock, IIdFactory ids, CancellationToken ct = default)
    {
        if (!TryParseKind(kind, out var parsedKind)) return ("bad_kind", null);
        if (format?.Trim().ToLowerInvariant() is not ("csv" or "json")) return ("bad_format", null);
        if (content is null || contentLength == 0) return ("empty_file", null);
        if (contentLength > MaxImportBytes) return ("file_too_large", null);

        List<Dictionary<string, string>>? rawRows;
        List<int>? malformedRows;
        string? parseError;

        if (format.Trim().ToLowerInvariant() == "csv")
            (parseError, rawRows, malformedRows) = ParseCsv(content, parsedKind);
        else
            (parseError, rawRows, malformedRows) = ParseJson(content, parsedKind);

        if (parseError is not null) return (parseError, null);
        if (rawRows!.Count > MaxImportRows) return ("too_many_rows", null);

        // Language can differ per row, so existing topics are looked up lazily, per language, as rows
        // that actually carry one are seen.
        var topicsByLang = new Dictionary<Language, HashSet<string>>();
        var batchTopics = new HashSet<string>();

        var results = new List<ImportRowResultDto>();
        var accepted = new List<Question>();

        for (var i = 0; i < rawRows.Count; i++)
        {
            var rowNumber = i + 1;

            if (malformedRows!.Contains(i))
            {
                results.Add(new ImportRowResultDto(rowNumber, null, false, "bad_row"));
                continue;
            }

            var fields = rawRows[i];
            var (question, prompt, error) = BindRow(fields, parsedKind, ids, clock.Now);

            if (question is null)
            {
                results.Add(new ImportRowResultDto(rowNumber, prompt, false, error));
                continue;
            }

            if (question.Topic is { Length: > 0 } topic)
            {
                if (!topicsByLang.TryGetValue(question.Lang, out var existingForLang))
                    topicsByLang[question.Lang] = existingForLang = [.. await questions.ExistingTopicsAsync(question.Lang, ct)];

                var key = $"{(int)question.Lang}|{topic}";
                if (existingForLang.Contains(topic) || batchTopics.Contains(key))
                {
                    results.Add(new ImportRowResultDto(rowNumber, prompt, false, "duplicate_topic"));
                    continue;
                }

                batchTopics.Add(key);
            }

            accepted.Add(question);
            results.Add(new ImportRowResultDto(rowNumber, prompt, true, null));
        }

        if (!dryRun && accepted.Count > 0)
            await questions.UpsertManyAsync(accepted, ct);

        var report = new ImportReportDto(dryRun, rawRows.Count, accepted.Count, rawRows.Count - accepted.Count, results);
        return (null, report);
    }

    // --- per-row binding -------------------------------------------------------------

    private static (Question? Question, string? Prompt, string? Error) BindRow(
        Dictionary<string, string> f, QuestionKind kind, IIdFactory ids, DateTimeOffset now)
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
        QuestionKind kind, Dictionary<string, string> f) => kind switch
    {
        QuestionKind.Sort => ([Field(f, "item1") ?? "", Field(f, "item2") ?? "", Field(f, "item3") ?? "", Field(f, "item4") ?? ""],
            0, null, null),
        QuestionKind.Map => ([], 0, TargetFor(f), Field(f, "baselayer")),
        _ => ([Field(f, "choice1") ?? "", Field(f, "choice2") ?? "", Field(f, "choice3") ?? "", Field(f, "choice4") ?? ""],
            ParseCorrectIndex(f), null, null)
    };

    private static int ParseCorrectIndex(Dictionary<string, string> f)
        => int.TryParse(Field(f, "correctindex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : -1;

    private static MapTargetDto? TargetFor(Dictionary<string, string> f)
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

    private static double? ParseDouble(Dictionary<string, string> f, string key)
        => double.TryParse(Field(f, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? Field(Dictionary<string, string> f, string key)
        => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

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

    // --- CSV ---------------------------------------------------------------------------

    private static (string? Error, List<Dictionary<string, string>>? Rows, List<int>? Malformed) ParseCsv(Stream content, QuestionKind kind)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        var records = ReadCsvRecords(reader.ReadToEnd());

        if (records.Count == 0) return (null, [], []);

        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        if (!header.ToHashSet().IsSupersetOf(RequiredCsvColumns(kind)))
            return ("bad_row", null, null);

        var rows = new List<Dictionary<string, string>>();
        var malformed = new List<int>();

        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            var map = new Dictionary<string, string>();

            if (record.Count != header.Count) malformed.Add(i - 1);
            else for (var c = 0; c < header.Count; c++) map[header[c]] = record[c];

            rows.Add(map);
        }

        return (null, rows, malformed);
    }

    /// <summary>The columns a row cannot be interpreted at all without — everything a kind needs to
    /// even attempt binding. Missing any of these fails the whole file before any row is read.</summary>
    private static IReadOnlyCollection<string> RequiredCsvColumns(QuestionKind kind)
        => (string[])["lang", "categoryid", "level", "prompt", .. KindFields[kind]];

    /// <summary>Minimal RFC4180: quoted fields, "" as an escaped quote, commas and newlines inside
    /// quotes. Records are lists of raw field strings, the first being the header.</summary>
    private static List<List<string>> ReadCsvRecords(string text)
    {
        var records = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        var sawAny = false;

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true; sawAny = true; i++; break;
                case ',':
                    current.Add(field.ToString()); field.Clear(); sawAny = true; i++; break;
                case '\r':
                    i++; break;
                case '\n':
                    current.Add(field.ToString()); field.Clear();
                    records.Add(current); current = []; sawAny = false; i++; break;
                default:
                    field.Append(c); sawAny = true; i++; break;
            }
        }

        if (sawAny || field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            records.Add(current);
        }

        return [.. records.Where(r => r.Count > 1 || r[0].Length > 0)];
    }

    // --- JSON --------------------------------------------------------------------------

    private static (string? Error, List<Dictionary<string, string>>? Rows, List<int>? Malformed) ParseJson(Stream content, QuestionKind kind)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException) { return ("bad_json", null, null); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ("bad_json", null, null);

            var rows = new List<Dictionary<string, string>>();
            var malformed = new List<int>();
            var idx = 0;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) { malformed.Add(idx); rows.Add([]); idx++; continue; }

                var map = new Dictionary<string, string>();
                foreach (var prop in element.EnumerateObject())
                    map[prop.Name.ToLowerInvariant()] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.Null => "",
                        _ => prop.Value.GetRawText()
                    };

                rows.Add(map);
                idx++;
            }

            return (null, rows, malformed);
        }
    }
}
