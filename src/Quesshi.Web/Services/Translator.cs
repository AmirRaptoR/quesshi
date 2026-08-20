using System.Net.Http.Json;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Loads a flat key/value JSON file per language from wwwroot/i18n and looks strings up by key.
/// No user-facing text lives in the code, so a translator can edit the JSON without a rebuild,
/// and switching language is a swap of the loaded table rather than a page reload.
/// </summary>
public sealed class Translator(HttpClient http)
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables = [];

    public string Lang { get; private set; } = "fa";

    public bool IsRtl => this["app.dir"] == "rtl";

    /// <summary>Missing keys render as the key itself, which makes the gap obvious rather than invisible.</summary>
    public string this[string key]
        => _tables.TryGetValue(Lang, out var table) && table.TryGetValue(key, out var value) ? value : key;

    public string Format(string key, params object[] args) => string.Format(this[key], args);

    /// <summary>
    /// A category's name in the language being read. The admin API cannot pick one for us — an
    /// admin session carries no player language — so the choice belongs here, next to the rest of it.
    /// </summary>
    public string Name(CategoryDto category)
        => Lang switch
        {
            "fa" => Or(category.NameFa, category.NameEn),
            "nl" => Or(category.NameNl, category.NameEn),
            _ => Or(category.NameEn, category.NameFa)
        };

    private static string Or(string first, string second) => first.Length > 0 ? first : second;

    /// <summary>Every language the app ships, in the order the picker offers them.</summary>
    public static readonly string[] All = ["fa", "en", "nl"];

    public async Task UseAsync(string lang)
    {
        Lang = lang;
        if (_tables.ContainsKey(lang)) return;

        try
        {
            _tables[lang] = await http.GetFromJsonAsync<Dictionary<string, string>>($"i18n/{lang}.json") ?? [];
        }
        catch (Exception)
        {
            // A missing translation file must not take the app down; keys will show through instead.
            _tables[lang] = [];
        }
    }

    private const char PersianZero = '\u06F0';

    /// <summary>Persian text reads badly with Latin digits, so swap them for the Persian set.</summary>
    public string Num(object? value)
    {
        var text = value?.ToString() ?? "";
        if (Lang != "fa") return text;

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsAsciiDigit(source[i]) ? (char)(PersianZero + (source[i] - '0')) : source[i];
        });
    }
}
