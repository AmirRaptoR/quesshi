using System.Net.Http.Json;
using Quesshi.Web.Plugins;

namespace Quesshi.Web.Services;

/// <summary>Loads the tenant's selected i18n table without assuming a fixed language enum.</summary>
public sealed class Translator(ClientHttp http) : IClientTranslator, IClientTranslationRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IReadOnlyDictionary<string, string>>> contributions = new(StringComparer.OrdinalIgnoreCase);
    public string Lang { get; private set; } = string.Empty;
    public string this[string key] => Find(key) ?? key;
    public string Format(string key, params object[] args) => string.Format(this[key], args);
    public string Or(string key, string fallback) => Find(key) ?? fallback;

    public void Register(ClientTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentException.ThrowIfNullOrWhiteSpace(translation.LanguageCode);
        ArgumentNullException.ThrowIfNull(translation.Values);
        if (!contributions.TryGetValue(translation.LanguageCode, out var languageContributions))
        {
            languageContributions = [];
            contributions.Add(translation.LanguageCode, languageContributions);
        }
        languageContributions.Add(translation.Values);
    }

    private const char PersianZero = '\u06F0';
    public string Num(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (!string.Equals(Lang, "fa", StringComparison.OrdinalIgnoreCase)) return text;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
                span[index] = char.IsAsciiDigit(source[index]) ? (char)(PersianZero + source[index] - '0') : source[index];
        });
    }

    public async Task UseAsync(string code)
    {
        Lang = code;
        if (tables.ContainsKey(code)) return;
        try
        {
            tables[code] = await http.GetFromJsonAsync<Dictionary<string, string>>($"i18n/{Uri.EscapeDataString(code)}.json") ?? [];
        }
        catch (Exception)
        {
            tables[code] = [];
        }
    }

    private string? Find(string key)
    {
        return FindContribution(Lang, key)
            ?? FindTable(Lang, key)
            ?? FindContribution("en", key)
            ?? FindTable("en", key);
    }

    private string? FindContribution(string languageCode, string key)
    {
        if (!contributions.TryGetValue(languageCode, out var languageContributions)) return null;
        foreach (var translation in languageContributions)
            if (translation.TryGetValue(key, out var value)) return value;
        return null;
    }

    private string? FindTable(string languageCode, string key) =>
        tables.TryGetValue(languageCode, out var table) && table.TryGetValue(key, out var value) ? value : null;
}
