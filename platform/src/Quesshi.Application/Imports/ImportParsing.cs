using System.Text;
using System.Text.Json;

namespace Quesshi.Application.Imports;

/// <summary>Parsed input for one tenant question-bank import request.</summary>
internal sealed record ImportDocument(IReadOnlyList<ImportDocumentRow> Rows);

/// <summary>One row from an import file. Malformed rows deliberately remain in the report.</summary>
internal sealed record ImportDocumentRow(int Number, IReadOnlyDictionary<string, string?> Fields, bool IsMalformed);

/// <summary>Shared CSV and JSON parsing for the platform importer.</summary>
internal static class ImportParsing
{
    internal const long MaximumBytes = 20 * 1024 * 1024;
    internal const int MaximumRows = 2_000;

    internal static async Task<(string? Error, ImportDocument? Document)> ParseAsync(
        string? format,
        Stream? content,
        long contentLength,
        IReadOnlyCollection<string> requiredCsvColumns,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = format?.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "json")) return ("bad_format", null);
        if (content is null || contentLength == 0) return ("empty_file", null);
        if (contentLength > MaximumBytes) return ("file_too_large", null);

        return normalizedFormat == "csv"
            ? await ParseCsvAsync(content, requiredCsvColumns, cancellationToken)
            : await ParseJsonAsync(content, cancellationToken);
    }

    private static async Task<(string? Error, ImportDocument? Document)> ParseCsvAsync(
        Stream content,
        IReadOnlyCollection<string> requiredColumns,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var records = ReadCsvRecords(await reader.ReadToEndAsync(cancellationToken));
        if (records.Count == 0) return ("empty_file", null);

        var headerRecord = records[0];
        var header = headerRecord.Fields.Select((value, index) => NormalizeHeader(value, index == 0)).ToArray();
        if (headerRecord.IsMalformed || !header.ToHashSet(StringComparer.OrdinalIgnoreCase).IsSupersetOf(requiredColumns))
            return ("missing_header", null);

        if (records.Count - 1 > MaximumRows) return ("too_many_rows", null);
        var rows = new List<ImportDocumentRow>(records.Count - 1);
        for (var index = 1; index < records.Count; index++)
        {
            var record = records[index];
            if (record.IsMalformed || record.Fields.Count != header.Length)
            {
                rows.Add(new(index, EmptyFields, true));
                continue;
            }

            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var field = 0; field < header.Length; field++) fields[header[field]] = record.Fields[field];
            rows.Add(new(index, fields, false));
        }
        return (null, new ImportDocument(rows));
    }

    private static async Task<(string? Error, ImportDocument? Document)> ParseJsonAsync(Stream content, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken); }
        catch (JsonException) { return ("bad_json", null); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return ("bad_json", null);
            if (document.RootElement.GetArrayLength() > MaximumRows) return ("too_many_rows", null);
            var rows = new List<ImportDocumentRow>(document.RootElement.GetArrayLength());
            var number = 1;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    rows.Add(new(number++, EmptyFields, true));
                    continue;
                }

                var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                    fields[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => property.Value.GetRawText(),
                    };
                rows.Add(new(number++, fields, false));
            }
            return (null, new ImportDocument(rows));
        }
    }

    private static string NormalizeHeader(string value, bool first) =>
        (first ? value.TrimStart('\uFEFF') : value).Trim();

    private static readonly IReadOnlyDictionary<string, string?> EmptyFields = new Dictionary<string, string?>();

    private sealed record CsvRecord(IReadOnlyList<string> Fields, bool IsMalformed);

    private static List<CsvRecord> ReadCsvRecords(string text)
    {
        var records = new List<CsvRecord>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var fieldStarted = false;
        var malformed = false;
        var sawValue = false;
        for (var index = 0; index < text.Length;)
        {
            var current = text[index++];
            if (quoted)
            {
                if (current == '"' && index < text.Length && text[index] == '"') { field.Append('"'); index++; }
                else if (current == '"') { quoted = false; closedQuote = true; }
                else field.Append(current);
                continue;
            }
            if (current == '"')
            {
                if (fieldStarted) malformed = true;
                else quoted = true;
                fieldStarted = true;
                sawValue = true;
                continue;
            }
            if (current == ',')
            {
                record.Add(field.ToString()); field.Clear(); fieldStarted = false; closedQuote = false; sawValue = true;
                continue;
            }
            if (current == '\r') continue;
            if (current == '\n')
            {
                record.Add(field.ToString()); field.Clear(); records.Add(new(record, malformed || quoted));
                record = []; fieldStarted = false; closedQuote = false; malformed = false; sawValue = false;
                continue;
            }
            if (closedQuote) malformed = true;
            field.Append(current); fieldStarted = true; sawValue = true;
        }
        if (sawValue || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(new(record, malformed || quoted));
        }
        return [.. records.Where(record => record.Fields.Count > 1 || record.Fields[0].Length > 0 || record.IsMalformed)];
    }
}
