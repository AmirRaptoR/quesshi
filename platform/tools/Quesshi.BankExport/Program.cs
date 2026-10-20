using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Quesshi.BankExport;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!ExportOptions.TryParse(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync(error);
            return 2;
        }

        try
        {
            var report = await new BankExporter(new MongoClient(options.ConnectionString), options).ExportAsync();
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(report, ExportJson.Options));
            return 0;
        }
        catch (Exception exception) when (exception is MongoException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }
}

public sealed record ExportOptions(string ConnectionString, string DatabaseName, string OutputDirectory, string MediaDirectory)
{
    public static bool TryParse(string[] args, out ExportOptions options, out string error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options = default!;
                error = "Usage: --connection <mongodb-url> --database <tenant-database> --output <bundle-directory> --media-root <legacy-wwwroot-media>.";
                return false;
            }
            values[args[index][2..]] = args[index + 1];
        }
        if (!values.TryGetValue("connection", out var connection) || !values.TryGetValue("database", out var database)
            || !values.TryGetValue("output", out var output) || !values.TryGetValue("media-root", out var media))
        {
            options = default!;
            error = "Usage: --connection <mongodb-url> --database <tenant-database> --output <bundle-directory> --media-root <legacy-wwwroot-media>.";
            return false;
        }
        options = new(connection, database, output, media);
        error = "";
        return true;
    }
}

public sealed class BankExporter(
    IMongoClient client,
    ExportOptions options,
    int maximumRowsPerFile = 2_000,
    int maximumBytesPerFile = 20 * 1024 * 1024)
{
    public const int MaximumRowsPerFile = 2_000;
    public const int MaximumBytesPerFile = 20 * 1024 * 1024;

    public async Task<ExportReport> ExportAsync(CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(options.OutputDirectory);
        PrepareOutput(output);
        var mediaRoot = Path.GetFullPath(options.MediaDirectory);
        var database = client.GetDatabase(options.DatabaseName);
        var questions = await database.GetCollection<LegacyQuestion>("questions").Find(FilterDefinition<LegacyQuestion>.Empty).ToListAsync(cancellationToken);
        var categories = await database.GetCollection<LegacyCategory>("categories").Find(FilterDefinition<LegacyCategory>.Empty).ToListAsync(cancellationToken);
        var settings = await database.GetCollection<LegacyContentSettings>("content_settings").Find(FilterDefinition<LegacyContentSettings>.Empty).FirstOrDefaultAsync(cancellationToken);

        var report = new ExportReport();
        var categorySlugs = categories.Where(category => !string.IsNullOrWhiteSpace(category.Id))
            .ToDictionary(category => category.Id, category => category.Id, StringComparer.Ordinal);
        await WriteJsonAsync(Path.Combine(output, "categories.json"), categories.Select(category => new ExportCategory(
            category.Id, LocalizedNames(category), NullIfWhiteSpace(category.Icon), NullIfWhiteSpace(category.Color), category.SortOrder, category.IsActive)).ToArray(), cancellationToken);
        await WriteJsonAsync(Path.Combine(output, "settings.json"), new ExportSettings(
            ResolveSettings(settings?.TriviaCategoryIds, categorySlugs, "trivia", report),
            ResolveSettings(settings?.VotingCategoryIds, categorySlugs, "voting", report)), cancellationToken);

        var rows = new Dictionary<string, List<ConvertedQuestion>>(StringComparer.Ordinal)
        {
            ["choice"] = [], ["order"] = [], ["map_pick"] = [], ["poll_choice"] = [], ["poll_roster"] = [],
        };
        foreach (var question in questions)
        {
            var converted = await ConvertAsync(question, categorySlugs, output, mediaRoot, report, cancellationToken);
            if (converted is null) continue;
            rows[converted.Kind].Add(converted);
        }
        foreach (var (kind, kindRows) in rows)
        {
            var written = await WriteQuestionPartsAsync(output, kind, kindRows, report, cancellationToken);
            foreach (var question in written)
            {
                report.ExportedKinds.Increment(question.Kind);
                report.ExportedLanguages.Increment((string)question.Row["language"]!);
                report.ExportedStatuses.Increment((string)question.Row["status"]!);
            }
        }
        await WriteJsonAsync(Path.Combine(output, "report.json"), report, cancellationToken);
        return report;
    }

    private static IReadOnlyList<string> ResolveSettings(IEnumerable<string>? categoryIds, IReadOnlyDictionary<string, string> slugs, string family, ExportReport report)
    {
        var resolved = new List<string>();
        foreach (var categoryId in categoryIds ?? [])
        {
            if (slugs.TryGetValue(categoryId, out var slug)) resolved.Add(slug);
            else report.MissingCategoryReferences.Add(new(categoryId, family, "settings"));
        }
        return resolved;
    }

    private static IReadOnlyDictionary<string, string> LocalizedNames(LegacyCategory category)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        AddName(names, "fa", category.NameFa);
        AddName(names, "en", category.NameEn);
        AddName(names, "nl", category.NameNl);
        return names;
    }

    private static void AddName(IDictionary<string, string> names, string language, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) names[language] = value;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<ConvertedQuestion?> ConvertAsync(LegacyQuestion question, IReadOnlyDictionary<string, string> categories, string output, string mediaRoot, ExportReport report, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(question.OwnerId) || question.Source == 3)
        {
            report.Skipped.Add(new(question.Id, "player_owned"));
            return null;
        }
        if (!categories.TryGetValue(question.CategoryId ?? "", out var categorySlug))
        {
            report.MissingCategoryReferences.Add(new(question.Id, question.CategoryId, "question"));
            report.Skipped.Add(new(question.Id, "category_missing"));
            return null;
        }
        if (!TryLanguage(question.Lang, out var language) || !TryStatus(question.Status, out var status) || !TrySource(question.Source, out var source))
        {
            report.Skipped.Add(new(question.Id, "unsupported_metadata"));
            return null;
        }
        if (string.IsNullOrWhiteSpace(question.Prompt) || (question.Family == 0 && question.Level is not (>= 1 and <= 5)))
        {
            report.Skipped.Add(new(question.Id, "question_invalid"));
            return null;
        }
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["language"] = language,
            ["categorySlug"] = categorySlug,
            ["prompt"] = question.Prompt,
            ["explanation"] = question.Explanation,
            ["topic"] = question.Topic,
            ["status"] = status,
            ["source"] = source,
        };
        if (question.Family == 0) row["level"] = question.Level;
        var kind = AddPayload(question, row, out var reason);
        if (kind is null)
        {
            report.Skipped.Add(new(question.Id, reason!));
            return null;
        }
        await AddMediaAsync(question, row, output, mediaRoot, report, cancellationToken);
        return new(question.Id, kind, row);
    }

    private static string? AddPayload(LegacyQuestion question, IDictionary<string, object?> row, out string? reason)
    {
        reason = null;
        if (question.Family == 0)
        {
            switch (question.Kind ?? 0)
            {
                case 0 when question.Choices.Count == 4 && question.CorrectIndex is >= 0 and < 4:
                    for (var index = 0; index < 4; index++) row[$"choice{index + 1}"] = question.Choices[index];
                    row["correctIndex"] = question.CorrectIndex;
                    return "choice";
                case 1 when question.Choices.Count == 4:
                    for (var index = 0; index < 4; index++) row[$"item{index + 1}"] = question.Choices[index];
                    return "order";
                case 2 when question.BaseLayer is 0 or 1:
                    row["baseLayer"] = question.BaseLayer == 0 ? "blank" : "borders";
                    if (question.TargetShape == 0 && !string.IsNullOrWhiteSpace(question.TargetCountryCode))
                    {
                        row["targetShape"] = "country";
                        row["countryCode"] = question.TargetCountryCode;
                        return "map_pick";
                    }
                    if (question.TargetShape == 1 && question.TargetLatitude.HasValue && question.TargetLongitude.HasValue && question.TargetRadiusKm.HasValue)
                    {
                        row["targetShape"] = "city";
                        row["latitude"] = question.TargetLatitude.Value;
                        row["longitude"] = question.TargetLongitude.Value;
                        row["radiusKm"] = question.TargetRadiusKm.Value;
                        return "map_pick";
                    }
                    break;
            }
            reason = "unsupported_trivia_shape";
            return null;
        }
        if (question.Family == 1)
        {
            if (question.AnswerSource == 1 && question.FixedChoices.Count is >= 2 and <= 8)
            {
                row["answerSource"] = "fixed";
                for (var index = 0; index < question.FixedChoices.Count; index++) row[$"choice{index + 1}"] = question.FixedChoices[index];
                return "poll_choice";
            }
            if (question.AnswerSource == 0)
            {
                row["answerSource"] = "participants";
                return "poll_roster";
            }
        }
        reason = "unsupported_voting_shape";
        return null;
    }

    private static async Task AddMediaAsync(LegacyQuestion question, IDictionary<string, object?> row, string output, string mediaRoot, ExportReport report, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question.MediaUrl)) return;
        if (Uri.TryCreate(question.MediaUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            row["mediaUrl"] = absolute.AbsoluteUri;
        }
        else if (TryMediaPath(question.MediaUrl, mediaRoot, out var relative, out var source))
        {
            row["mediaUrl"] = "media/" + relative.Replace(Path.DirectorySeparatorChar, '/');
            if (File.Exists(source))
            {
                var destination = Path.Combine(output, "media", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = File.OpenRead(source);
                await using var target = File.Create(destination);
                await input.CopyToAsync(target, cancellationToken);
            }
            else report.MissingMedia.Add(new(question.Id, question.MediaUrl, "question"));
        }
        else
        {
            report.OutsideMedia.Add(new(question.Id, question.MediaUrl, "question"));
            row["mediaUrl"] = question.MediaUrl;
        }
        if (TryMediaKind(question.MediaKind, out var kind)) row["mediaKind"] = kind;
        if (!string.IsNullOrWhiteSpace(question.MediaAttribution)) row["mediaAttribution"] = question.MediaAttribution;
    }

    private static bool TryMediaPath(string value, string root, out string relative, out string source)
    {
        var candidate = value.Replace('\\', '/');
        if (candidate.StartsWith("/media/", StringComparison.Ordinal)) candidate = candidate[7..];
        else if (candidate.StartsWith("media/", StringComparison.Ordinal)) candidate = candidate[6..];
        if (Path.IsPathRooted(candidate) || candidate.Split('/', StringSplitOptions.None).Any(part => part is "" or "." or ".."))
        {
            relative = source = "";
            return false;
        }
        relative = candidate.Replace('/', Path.DirectorySeparatorChar);
        source = Path.GetFullPath(Path.Combine(root, relative));
        return source.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryLanguage(int value, out string language) => (language = value switch { 0 => "fa", 1 => "en", 2 => "nl", _ => "" }) != "";
    private static bool TryStatus(int value, out string status) => (status = value switch { 0 => "pending", 1 => "approved", 2 => "rejected", _ => "" }) != "";
    private static bool TrySource(int value, out string source) => (source = value switch { 0 => "seed", 1 => "ai", 2 => "admin", _ => "" }) != "";
    private static bool TryMediaKind(int value, out string kind) => (kind = value switch { 1 => "image", 2 => "audio", 3 => "video", _ => "" }) != "";

    private static void PrepareOutput(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("The output directory must be empty.");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "media"));
    }
    private async Task<IReadOnlyList<ConvertedQuestion>> WriteQuestionPartsAsync(string output, string kind, IReadOnlyList<ConvertedQuestion> rows, ExportReport report, CancellationToken cancellationToken)
    {
        var parts = new List<List<SerializedQuestion>>();
        List<SerializedQuestion>? part = null;
        var partBytes = 2;
        var written = new List<ConvertedQuestion>();
        foreach (var question in rows)
        {
            var serialized = new SerializedQuestion(question, JsonSerializer.SerializeToUtf8Bytes(question.Row, CompactJson.Options));
            if (serialized.Json.Length + 2 > maximumBytesPerFile)
            {
                report.Skipped.Add(new(question.Id, "row_exceeds_import_byte_limit"));
                continue;
            }
            if (part is null ||
                part.Count == maximumRowsPerFile ||
                partBytes + (part.Count == 0 ? 0 : 1) + serialized.Json.Length > maximumBytesPerFile)
            {
                part = [];
                parts.Add(part);
                partBytes = 2;
            }

            partBytes += (part.Count == 0 ? 0 : 1) + serialized.Json.Length;
            part.Add(serialized);
            written.Add(question);
        }
        for (var index = 0; index < parts.Count; index++)
        {
            var name = parts.Count == 1 ? $"{kind}.json" : $"{kind}-{index + 1:D3}.json";
            await WriteQuestionPartAsync(Path.Combine(output, name), parts[index], cancellationToken);
        }
        return written;
    }

    private static async Task WriteQuestionPartAsync(string path, IReadOnlyList<SerializedQuestion> rows, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await stream.WriteAsync("["u8.ToArray(), cancellationToken);
        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0) await stream.WriteAsync(","u8.ToArray(), cancellationToken);
            await stream.WriteAsync(rows[index].Json, cancellationToken);
        }
        await stream.WriteAsync("]"u8.ToArray(), cancellationToken);
    }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, ExportJson.Options, cancellationToken);
    }
}

public sealed record ConvertedQuestion(string Id, string Kind, Dictionary<string, object?> Row);
public sealed record SerializedQuestion(ConvertedQuestion Question, byte[] Json);
public sealed record ExportCategory(
    string Slug,
    IReadOnlyDictionary<string, string> Names,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color,
    int SortOrder,
    bool IsActive);
public sealed record ExportSettings(IReadOnlyList<string> TriviaCategorySlugs, IReadOnlyList<string> VotingCategorySlugs);
public sealed record ExportReference(string QuestionId, string? Value, string Scope);
public sealed record SkippedQuestion(string QuestionId, string Reason);
public sealed record ExportReport
{
    public ExportCounts ExportedKinds { get; } = new();
    public ExportCounts ExportedLanguages { get; } = new();
    public ExportCounts ExportedStatuses { get; } = new();
    public List<SkippedQuestion> Skipped { get; } = [];
    public List<ExportReference> MissingMedia { get; } = [];
    public List<ExportReference> OutsideMedia { get; } = [];
    public List<ExportReference> MissingCategoryReferences { get; } = [];
}
public sealed class ExportCounts : Dictionary<string, int>
{
    public void Increment(string key) => this[key] = TryGetValue(key, out var value) ? value + 1 : 1;
}
public static class ExportJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
public static class CompactJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

[BsonIgnoreExtraElements]
public sealed class LegacyQuestion
{
    [BsonId] public string Id { get; set; } = "";
    public int Family { get; set; }
    public string? OwnerId { get; set; }
    public int Lang { get; set; }
    public string? CategoryId { get; set; }
    public int? Level { get; set; }
    public string? Prompt { get; set; }
    public List<string> Choices { get; set; } = [];
    public int? CorrectIndex { get; set; }
    public int MediaKind { get; set; }
    public string? MediaUrl { get; set; }
    public string? MediaAttribution { get; set; }
    public string? Explanation { get; set; }
    public string? Topic { get; set; }
    public int Status { get; set; }
    public int Source { get; set; }
    public List<string> FixedChoices { get; set; } = [];
    public int? Kind { get; set; }
    public int? TargetShape { get; set; }
    public string? TargetCountryCode { get; set; }
    public double? TargetLatitude { get; set; }
    public double? TargetLongitude { get; set; }
    public double? TargetRadiusKm { get; set; }
    public int? BaseLayer { get; set; }
    public int AnswerSource { get; set; }
}
[BsonIgnoreExtraElements]
public sealed class LegacyCategory
{
    [BsonId] public string Id { get; set; } = "";
    public string? NameFa { get; set; }
    public string? NameEn { get; set; }
    public string? NameNl { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
[BsonIgnoreExtraElements]
public sealed class LegacyContentSettings
{
    [BsonId] public string Id { get; set; } = "settings";
    public List<string> TriviaCategoryIds { get; set; } = [];
    public List<string> VotingCategoryIds { get; set; } = [];
}
