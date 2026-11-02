using System.Reflection;
using System.Text.Json;

namespace Quesshi.Server.StaticPages;

/// <summary>The client's translation tables, embedded so the server renders the same words the app shows.</summary>
public static class StaticTexts
{
    private const string FallbackLanguage = "en";
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Tables = new(Load);

    /// <summary>The table for a language with English filling any key it lacks.</summary>
    public static IReadOnlyDictionary<string, string> For(string language)
    {
        var tables = Tables.Value;
        var fallback = tables.GetValueOrDefault(FallbackLanguage) ?? new Dictionary<string, string>();
        if (!tables.TryGetValue(language, out var table) || ReferenceEquals(table, fallback)) return fallback;
        var merged = new Dictionary<string, string>(fallback, StringComparer.Ordinal);
        foreach (var (key, value) in table) merged[key] = value;
        return merged;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var assembly = typeof(StaticTexts).Assembly;
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("i18n/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            result[Path.GetFileNameWithoutExtension(name)] = table;
        }
        return result;
    }
}
