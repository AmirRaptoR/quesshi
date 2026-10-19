using System.Globalization;

namespace Quesshi.Kinds;

/// <summary>Maps one legacy question shape to a kind payload.</summary>
public interface IImports
{
    IReadOnlyList<string> CsvColumns { get; }
    IReadOnlyList<string> JsonFields { get; }
    bool Matches(string? legacyKind, ImportRow row);
}

/// <summary>Maps one legacy question shape to a typed kind payload.</summary>
public interface IImports<TPayload> : IImports where TPayload : class
{
    ImportExample CsvExample { get; }
    ImportExample JsonExample { get; }
    TPayload Convert(ImportRow row);
}

/// <summary>A normalized legacy row whose field names are case-insensitive.</summary>
public sealed class ImportRow
{
    private readonly IReadOnlyDictionary<string, string?> fields;

    public ImportRow(IReadOnlyDictionary<string, string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in fields) normalized[name] = value;
        this.fields = normalized;
    }

    public string? Field(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }
}

/// <summary>Kind-specific fields for one downloadable import example.</summary>
public sealed record ImportExample(IReadOnlyDictionary<string, string?> Fields);

internal static class ImportValues
{
    public static int Integer(ImportRow row, string field, int fallback) =>
        int.TryParse(row.Field(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static double? Number(ImportRow row, string field) =>
        double.TryParse(row.Field(field), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
