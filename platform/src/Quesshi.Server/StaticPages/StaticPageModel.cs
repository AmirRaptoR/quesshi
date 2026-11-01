namespace Quesshi.Server.StaticPages;

/// <summary>What a static page needs to render one tenant in one language, with no per-visitor data.</summary>
public sealed class StaticPageModel(
    string language,
    string direction,
    string appName,
    string? landingText,
    string origin,
    string imageUrl,
    IReadOnlyDictionary<string, string> texts)
{
    public string Language { get; } = language;
    public string Direction { get; } = direction;
    public string AppName { get; } = appName;
    public string? LandingText { get; } = landingText;
    public string Origin { get; } = origin;
    public string ImageUrl { get; } = imageUrl;

    /// <summary>The translated text for a key, or the key itself when the table has no entry.</summary>
    public string this[string key] => texts.GetValueOrDefault(key) ?? key;
}
