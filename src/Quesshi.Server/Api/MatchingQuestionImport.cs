using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>
/// Bulk-imports matching questions. This is deliberately a separate importer from
/// <see cref="QuestionImport": matching questions have no level, correct answer, or trivia kind.
/// </summary>
public static class MatchingQuestionImport
{
    private static readonly string[] RequiredColumns = ["lang", "categoryid", "prompt", "answersource"];
    private static readonly string[] ChoiceColumns = ["choice1", "choice2", "choice3", "choice4",
        "choice5", "choice6", "choice7", "choice8"];

    public static async Task<(string? RequestError, ImportReportDto? Report)> RunAsync(
        string? format, Stream? content, long contentLength, bool dryRun,
        IMatchingQuestionRepository questions, IMatchingCategoryRepository categories,
        IClock clock, IIdFactory ids, CancellationToken ct = default)
    {
        var normalizedFormat = format?.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "json")) return ("bad_format", null);
        if (content is null || contentLength == 0) return ("empty_file", null);
        if (contentLength > QuestionImport.MaxImportBytes) return ("file_too_large", null);

        List<Dictionary<string, string>>? rawRows;
        List<int>? malformedRows;
        string? parseError;
        if (normalizedFormat == "csv")
            (parseError, rawRows, malformedRows) = ParseCsv(content);
        else
            (parseError, rawRows, malformedRows) = ParseJson(content);

        if (parseError is not null) return (parseError, null);
        if (rawRows!.Count > QuestionImport.MaxImportRows) return ("too_many_rows", null);

        var rows = new List<ImportRowResultDto>(rawRows.Count);
        var candidates = new List<(int Row, MatchingQuestion Question, string? Prompt)>();
        var topicsByLang = new Dictionary<Language, HashSet<string>>();
        var fileTopics = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < rawRows.Count; i++)
        {
            var rowNumber = i + 1;
            if (malformedRows!.Contains(i))
            {
                rows.Add(new ImportRowResultDto(rowNumber, null, false, "bad_row"));
                continue;
            }

            var fields = rawRows[i];
            var prompt = Field(fields, "prompt");
            var (question, error) = await BindRowAsync(fields, categories, ids, clock.Now, ct);
            if (question is null)
            {
                rows.Add(new ImportRowResultDto(rowNumber, prompt, false, error));
                continue;
            }

            if (question.Topic is { Length: > 0 } topic)
            {
                if (!topicsByLang.TryGetValue(question.Lang, out var existing))
                    topicsByLang[question.Lang] = existing = [.. await questions.ExistingTopicsAsync(question.Lang, ct)];

                var key = $"{(int)question.Lang}|{topic}";
                if (existing.Contains(topic) || !fileTopics.Add(key))
                {
                    rows.Add(new ImportRowResultDto(rowNumber, question.Prompt, false, "duplicate_topic"));
                    continue;
                }
            }

            candidates.Add((rowNumber, question, question.Prompt));
            rows.Add(new ImportRowResultDto(rowNumber, question.Prompt, true, null));
        }

        // Write one row at a time. Besides keeping the report honest if a unique index races another
        // writer, this lets us name the exact row that lost rather than returning a lower batch count.
        if (!dryRun)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    await questions.UpsertAsync(candidate.Question, ct);
                }
                catch (InvalidOperationException)
                {
                    ReplaceResult(rows, candidate.Row, candidate.Prompt, "duplicate_topic");
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
                {
                    ReplaceResult(rows, candidate.Row, candidate.Prompt, "duplicate_topic");
                }
            }
        }

        var accepted = rows.Count(r => r.Accepted);
        return (null, new ImportReportDto(dryRun, rows.Count, accepted, rows.Count - accepted, rows));
    }

    private static async Task<(MatchingQuestion? Question, string? Error)> BindRowAsync(
        Dictionary<string, string> fields, IMatchingCategoryRepository categories,
        IIdFactory ids, DateTimeOffset now, CancellationToken ct)
    {
        var langRaw = Field(fields, "lang")?.ToLowerInvariant();
        if (langRaw is not ("fa" or "en" or "nl")) return (null, "bad_lang");
        var lang = langRaw.ToLanguage();

        var categoryId = Field(fields, "categoryid")?.ToLowerInvariant();
        if (categoryId is null) return (null, "unknown_category");
        if (!categoryId.StartsWith("m-", StringComparison.Ordinal)) categoryId = "m-" + categoryId;
        var category = await categories.GetAsync(categoryId, ct);
        if (category is null) return (null, "unknown_category");
        if (!category.IsActive) return (null, "inactive_category");

        var prompt = Field(fields, "prompt");
        if (prompt is null) return (null, "blank_prompt");

        var sourceRaw = Field(fields, "answersource")?.ToLowerInvariant();
        if (sourceRaw is not ("participants" or "fixed")) return (null, "bad_answer_source");
        var source = sourceRaw == "fixed" ? MatchingAnswerSource.Fixed : MatchingAnswerSource.Participants;
        var choices = ChoiceColumns.Select(key => Field(fields, key)).Where(value => value is not null)
            .Select(value => value!).ToList();

        if (source == MatchingAnswerSource.Participants && choices.Count != 0)
            return (null, "choices_not_allowed");
        if (source == MatchingAnswerSource.Fixed && choices.Count < MatchingRules.MinFixedChoices)
            return (null, "too_few_choices");
        if (source == MatchingAnswerSource.Fixed && choices.Count > MatchingRules.MaxFixedChoices)
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
        var question = MatchingQuestion.Create(ids.NewId(), lang, category.Id, prompt, source, choices, now,
            media, QuestionSource.Admin, status, topic);
        return (question, null);
    }

    private static string? Field(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static void ReplaceResult(List<ImportRowResultDto> rows, int row, string? prompt, string error)
        => rows[row - 1] = new ImportRowResultDto(row, prompt, false, error);

    private static (string? Error, List<Dictionary<string, string>>? Rows, List<int>? Malformed) ParseCsv(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var records = ReadCsvRecords(reader.ReadToEnd());
        if (records.Count == 0) return (null, [], []);

        var headerRecord = records[0];
        var header = headerRecord.Fields.Select((value, index) => index == 0 ? value.TrimStart('\uFEFF').Trim().ToLowerInvariant() : value.Trim().ToLowerInvariant()).ToList();
        if (headerRecord.Malformed || !header.ToHashSet(StringComparer.Ordinal).IsSupersetOf(RequiredColumns))
            return ("bad_row", null, null);

        var rows = new List<Dictionary<string, string>>();
        var malformed = new List<int>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (record.Malformed || record.Fields.Count != header.Count) malformed.Add(i - 1);
            else for (var c = 0; c < header.Count; c++) map[header[c]] = record.Fields[c];
            rows.Add(map);
        }

        return (null, rows, malformed);
    }

    private sealed record CsvRecord(List<string> Fields, bool Malformed);

    private static List<CsvRecord> ReadCsvRecords(string text)
    {
        var records = new List<CsvRecord>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var quoteClosed = false;
        var fieldStarted = false;
        var malformed = false;
        var sawAny = false;
        for (var i = 0; i < text.Length;)
        {
            var c = text[i++];
            if (quoted)
            {
                if (c == '"' && i < text.Length && text[i] == '"') { field.Append('"'); i++; }
                else if (c == '"') { quoted = false; quoteClosed = true; }
                else field.Append(c);
                continue;
            }
            if (c == '"')
            {
                // A quote is legal only at the beginning of a field. Quotes in ordinary text are
                // malformed, as is a quote after a quoted field has already been closed.
                if (fieldStarted) malformed = true;
                else quoted = true;
                fieldStarted = true;
                sawAny = true;
                continue;
            }
            if (c == ',')
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                quoteClosed = false;
                sawAny = true;
                continue;
            }
            if (c == '\r') continue;
            if (c == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                records.Add(new CsvRecord(record, malformed || quoted));
                record = [];
                fieldStarted = false;
                quoteClosed = false;
                malformed = false;
                sawAny = false;
                continue;
            }
            if (quoteClosed) malformed = true;
            field.Append(c);
            fieldStarted = true;
            sawAny = true;
        }
        if (sawAny || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(new CsvRecord(record, malformed || quoted));
        }
        return [.. records.Where(r => r.Fields.Count > 1 || r.Fields[0].Length > 0 || r.Malformed)];
    }

    private static (string? Error, List<Dictionary<string, string>>? Rows, List<int>? Malformed) ParseJson(Stream content)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(content); }
        catch (JsonException) { return ("bad_json", null, null); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return ("bad_json", null, null);
            var rows = new List<Dictionary<string, string>>();
            var malformed = new List<int>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) { malformed.Add(index); rows.Add([]); index++; continue; }
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    map[property.Name.ToLowerInvariant()] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.Null => "",
                        _ => property.Value.GetRawText()
                    };
                rows.Add(map);
                index++;
            }
            return (null, rows, malformed);
        }
    }
}
