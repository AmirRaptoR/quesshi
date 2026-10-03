using System.Text;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Shared file validation, parsing, topic deduplication, dry-run and report flow.</summary>
internal static class QuestionImportPipeline
{
    internal sealed record BoundRow<T>(T Value, string? Prompt, Language Language, string? Topic);
    private sealed record CsvRecord(List<string> Fields, bool Malformed);

    public static async Task<(string? RequestError, ImportReportDto? Report)> RunAsync<T>(
        string? format, Stream? content, long contentLength, bool dryRun, IReadOnlyCollection<string> requiredColumns,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<(BoundRow<T>? Row, string? Error)>> bind,
        Func<Language, CancellationToken, Task<IReadOnlySet<string>>> existingTopics,
        Func<IReadOnlyList<(int Row, BoundRow<T> Bound)>, CancellationToken, Task<IReadOnlySet<int>>> write,
        CancellationToken ct = default) where T : class
    {
        var normalizedFormat = format?.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "json")) return ("bad_format", null);
        if (content is null || contentLength == 0) return ("empty_file", null);
        if (contentLength > QuestionImport.MaxImportBytes) return ("file_too_large", null);

        var (parseError, rawRows, malformedRows) = normalizedFormat == "csv"
            ? ParseCsv(content, requiredColumns)
            : ParseJson(content);
        if (parseError is not null) return (parseError, null);
        if (rawRows!.Count > QuestionImport.MaxImportRows) return ("too_many_rows", null);

        var results = new List<ImportRowResultDto>(rawRows.Count);
        var accepted = new List<(int Row, BoundRow<T> Bound)>();
        var topicsByLanguage = new Dictionary<Language, IReadOnlySet<string>>();
        var fileTopics = new Dictionary<Language, HashSet<string>>();

        for (var i = 0; i < rawRows.Count; i++)
        {
            var rowNumber = i + 1;
            if (malformedRows!.Contains(i))
            {
                results.Add(new ImportRowResultDto(rowNumber, null, false, "bad_row"));
                continue;
            }

            var (bound, error) = await bind(rawRows[i], ct);
            if (bound is null)
            {
                results.Add(new ImportRowResultDto(rowNumber, Field(rawRows[i], "prompt"), false, error));
                continue;
            }

            if (!string.IsNullOrEmpty(bound.Topic))
            {
                if (!topicsByLanguage.TryGetValue(bound.Language, out var existing))
                    topicsByLanguage[bound.Language] = existing = await existingTopics(bound.Language, ct);
                if (!fileTopics.TryGetValue(bound.Language, out var inFile))
                    fileTopics[bound.Language] = inFile = new HashSet<string>(StringComparer.Ordinal);
                if (existing.Contains(bound.Topic) || !inFile.Add(bound.Topic))
                {
                    results.Add(new ImportRowResultDto(rowNumber, bound.Prompt, false, "duplicate_topic"));
                    continue;
                }
            }

            accepted.Add((rowNumber, bound));
            results.Add(new ImportRowResultDto(rowNumber, bound.Prompt, true, null));
        }

        if (!dryRun && accepted.Count > 0)
        {
            var rejected = await write(accepted, ct);
            foreach (var row in rejected)
                results[row - 1] = new ImportRowResultDto(row, results[row - 1].Prompt, false, "duplicate_topic");
        }

        var acceptedCount = results.Count(result => result.Accepted);
        return (null, new ImportReportDto(dryRun, rawRows.Count, acceptedCount, rawRows.Count - acceptedCount, results));
    }

    private static (string? Error, List<Dictionary<string, string>>? Rows, List<int>? Malformed) ParseCsv(
        Stream content, IReadOnlyCollection<string> requiredColumns)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var records = ReadCsvRecords(reader.ReadToEnd());
        if (records.Count == 0) return (null, [], []);
        var header = records[0].Fields.Select((value, index) => index == 0
            ? value.TrimStart('\uFEFF').Trim().ToLowerInvariant()
            : value.Trim().ToLowerInvariant()).ToList();
        if (records[0].Malformed || !header.ToHashSet(StringComparer.Ordinal).IsSupersetOf(requiredColumns))
            return ("bad_row", null, null);

        var rows = new List<Dictionary<string, string>>();
        var malformed = new List<int>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (record.Malformed || record.Fields.Count != header.Count) malformed.Add(i - 1);
            else for (var column = 0; column < header.Count; column++) fields[header[column]] = record.Fields[column];
            rows.Add(fields);
        }
        return (null, rows, malformed);
    }

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
                if (fieldStarted) malformed = true;
                else quoted = true;
                fieldStarted = true;
                sawAny = true;
                continue;
            }
            if (c == ',')
            {
                record.Add(field.ToString()); field.Clear(); fieldStarted = false; quoteClosed = false; sawAny = true; continue;
            }
            if (c == '\r') continue;
            if (c == '\n')
            {
                record.Add(field.ToString()); field.Clear(); records.Add(new CsvRecord(record, malformed || quoted));
                record = []; fieldStarted = false; quoteClosed = false; malformed = false; sawAny = false; continue;
            }
            if (quoteClosed) malformed = true;
            field.Append(c); fieldStarted = true; sawAny = true;
        }
        if (sawAny || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString()); records.Add(new CsvRecord(record, malformed || quoted));
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
                if (element.ValueKind != JsonValueKind.Object) { malformed.Add(index++); rows.Add([]); continue; }
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    fields[property.Name.ToLowerInvariant()] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.Null => "",
                        _ => property.Value.GetRawText()
                    };
                rows.Add(fields);
                index++;
            }
            return (null, rows, malformed);
        }
    }

    internal static string? Field(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
