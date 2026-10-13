using System.Net.Http.Json;

namespace Quesshi.Web.Services;

/// <summary>Loads the tenant's selected i18n table without assuming a fixed language enum.</summary>
public sealed class Translator(ClientHttp http)
{
    private readonly Dictionary<string, Dictionary<string, string>> tables = new(StringComparer.OrdinalIgnoreCase);
    public string Lang { get; private set; } = string.Empty;
    public string this[string key] => tables.TryGetValue(Lang, out var table) && table.TryGetValue(key, out var value) ? value : key;
    public string Format(string key, params object[] args) => string.Format(this[key], args);
    public string Or(string key, string fallback) => tables.TryGetValue(Lang, out var table) && table.TryGetValue(key, out var value) ? value : fallback;

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
}
