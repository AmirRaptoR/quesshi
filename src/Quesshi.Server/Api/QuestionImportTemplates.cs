using System.Text;
using System.Text.Json;
using Quesshi.Domain;

namespace Quesshi.Server.Api;

/// <summary>
/// The six downloadable templates — one per <see cref="QuestionKind"/>, in CSV and in JSON — so an
/// admin never has to guess a column name. Each is a header (CSV) or the equivalent keys (JSON) plus
/// one filled example row, matching exactly the fields <see cref="QuestionImport"/> reads.
/// </summary>
public static class QuestionImportTemplates
{
    public sealed record TemplateFile(byte[] Content, string ContentType, string FileName);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static (string? Error, TemplateFile? File) Build(string? kind, string? format)
    {
        if (string.IsNullOrWhiteSpace(kind) || !Enum.TryParse<QuestionKind>(kind, true, out var parsedKind) || !Enum.IsDefined(parsedKind))
            return ("bad_kind", null);

        var normalizedFormat = format?.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "json")) return ("bad_format", null);

        var kindName = parsedKind.ToString().ToLowerInvariant();
        var row = ExampleRow(parsedKind);

        return normalizedFormat == "csv"
            ? (null, new TemplateFile(BuildCsv(row), "text/csv", $"{kindName}-template.csv"))
            : (null, new TemplateFile(BuildJson(row), "application/json", $"{kindName}-template.json"));
    }

    /// <summary>Column order doubles as the order the JSON example's keys are written in.</summary>
    private static List<(string Column, string Value)> ExampleRow(QuestionKind kind)
    {
        var common = new List<(string, string)>
        {
            ("lang", "en"), ("categoryId", "geography"), ("level", "2"),
        };

        List<(string, string)> shape = kind switch
        {
            QuestionKind.Sort =>
            [
                ("prompt", "Order these cities by population, largest first"),
                ("item1", "Tokyo"), ("item2", "Delhi"), ("item3", "Cairo"), ("item4", "Lima"),
            ],
            QuestionKind.Map =>
            [
                ("prompt", "Which country is shaped like this?"),
                ("targetShape", "country"), ("countryCode", "NL"),
                ("latitude", ""), ("longitude", ""), ("radiusKm", ""), ("baseLayer", "borders"),
            ],
            _ =>
            [
                ("prompt", "Which sea has no coastline?"),
                ("choice1", "Sargasso Sea"), ("choice2", "Baltic Sea"), ("choice3", "Red Sea"), ("choice4", "Black Sea"),
                ("correctIndex", "0"),
            ]
        };

        var tail = new List<(string, string)>
        {
            ("explanation", ""), ("mediaUrl", ""), ("mediaKind", ""), ("status", "pending"),
            ("subject", ""), ("aspect", ""),
        };

        return [.. common, .. shape, .. tail];
    }

    private static byte[] BuildCsv(List<(string Column, string Value)> row)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', row.Select(r => CsvField(r.Column)))).Append("\r\n");
        sb.Append(string.Join(',', row.Select(r => CsvField(r.Value)))).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static byte[] BuildJson(List<(string Column, string Value)> row)
    {
        var obj = row.ToDictionary(r => r.Column, r => r.Value, StringComparer.Ordinal);
        return JsonSerializer.SerializeToUtf8Bytes(new[] { obj }, Json);
    }
}
