using System.Text;
using System.Text.Json;
using Quesshi.Domain;

namespace Quesshi.Server.Api;

/// <summary>Downloadable CSV and JSON examples for the matching-only importer.</summary>
public static class MatchingQuestionImportTemplates
{
    public sealed record TemplateFile(byte[] Content, string ContentType, string FileName);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Columns = ["lang", "categoryId", "prompt", "answerSource",
        "choice1", "choice2", "choice3", "choice4", "choice5", "choice6", "choice7", "choice8",
        "mediaUrl", "mediaKind", "subject", "aspect", "status"];

    public static (string? Error, TemplateFile? File) Build(string? format)
    {
        var normalized = format?.Trim().ToLowerInvariant();
        if (normalized is not ("csv" or "json")) return ("bad_format", null);

        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lang"] = "en", ["categoryId"] = "m-partners",
            ["prompt"] = "Who is most likely to leave the lights on?",
            ["answerSource"] = "participants", ["choice1"] = "", ["choice2"] = "", ["choice3"] = "", ["choice4"] = "",
            ["choice5"] = "", ["choice6"] = "", ["choice7"] = "", ["choice8"] = "",
            ["mediaUrl"] = "", ["mediaKind"] = "", ["subject"] = "household", ["aspect"] = "lights",
            ["status"] = "pending"
        };

        return normalized == "csv"
            ? (null, new TemplateFile(BuildCsv(row), "text/csv", "matching-template.csv"))
            : (null, new TemplateFile(JsonSerializer.SerializeToUtf8Bytes(new[] { row }, Json), "application/json", "matching-template.json"));
    }

    private static byte[] BuildCsv(IReadOnlyDictionary<string, string> row)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Columns.Select(CsvField))).Append("\r\n");
        sb.Append(string.Join(',', Columns.Select(column => CsvField(row[column])))).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
