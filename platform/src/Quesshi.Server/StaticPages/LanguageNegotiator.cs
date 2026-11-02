using Microsoft.Net.Http.Headers;

namespace Quesshi.Server.StaticPages;

/// <summary>A language a tenant offers, with its text direction (<c>ltr</c> or <c>rtl</c>).</summary>
public sealed record OfferedLanguage(string Code, string Direction);

/// <summary>
/// Picks the language of a static page by the rule the app uses: the visitor's saved choice (the
/// <c>lang</c> cookie), else the browser's language when the tenant offers it, else the tenant default.
/// </summary>
public static class LanguageNegotiator
{
    public static OfferedLanguage Choose(
        IReadOnlyList<OfferedLanguage> offered,
        string defaultLanguage,
        string? cookie,
        string? acceptLanguage)
    {
        if (Find(offered, cookie) is { } saved) return saved;

        foreach (var wanted in Preferences(acceptLanguage))
        {
            if (Find(offered, wanted) is { } match) return match;
            var dash = wanted.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && Find(offered, wanted[..dash]) is { } primary) return primary;
        }

        return Find(offered, defaultLanguage) ?? offered.FirstOrDefault() ?? new OfferedLanguage(defaultLanguage, "ltr");
    }

    private static OfferedLanguage? Find(IReadOnlyList<OfferedLanguage> offered, string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : offered.FirstOrDefault(language => string.Equals(language.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Preferences(string? header) =>
        StringWithQualityHeaderValue.TryParseList(header is null ? [] : [header], out var values)
            ? values
                .Where(value => value.Quality is null or > 0 && value.Value.HasValue && value.Value.Value != "*")
                .OrderByDescending(value => value.Quality ?? 1.0)
                .Select(value => value.Value.Value!)
            : [];
}
