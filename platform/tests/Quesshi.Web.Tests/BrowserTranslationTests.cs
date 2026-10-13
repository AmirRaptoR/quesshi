using System.Text.Json;

namespace Quesshi.Web.Tests;

/// <summary>
/// The server-side mail translations have a parity test (<c>ServerTranslationTests</c>); the browser's
/// three files under <c>wwwroot/i18n</c> did not, so a key added to one language and forgotten in
/// another rendered as the raw key with nobody noticing until a player saw it. See #13.
/// </summary>
public class BrowserTranslationTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "i18n");
    private static readonly string[] Languages = ["en", "fa", "nl"];

    private static Dictionary<string, string> Read(string lang)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(
               File.ReadAllText(Path.Combine(Folder, $"{lang}.json")))!;

    [Theory]
    [InlineData("fa")]
    [InlineData("nl")]
    public void Every_language_has_every_key_english_has(string lang)
    {
        var english = Read("en");
        var table = Read(lang);

        var missing = english.Keys.Except(table.Keys).ToList();
        Assert.True(missing.Count == 0, $"{lang}.json is missing: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fa")]
    [InlineData("nl")]
    public void No_language_has_a_key_the_others_lack(string lang)
    {
        var others = Languages.Where(l => l != lang).SelectMany(l => Read(l).Keys).ToHashSet();
        var table = Read(lang);

        var extra = table.Keys.Except(others).ToList();
        Assert.True(extra.Count == 0, $"{lang}.json has keys no other language defines: {string.Join(", ", extra)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fa")]
    [InlineData("nl")]
    public void No_language_has_an_empty_value(string lang)
    {
        var table = Read(lang);

        Assert.All(table, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value),
            $"{lang}.json has an empty value for {entry.Key}"));
    }
}
