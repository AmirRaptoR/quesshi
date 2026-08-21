using System.Text.Json;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

/// <summary>
/// The emails are the one place a missing translation is invisible until it reaches somebody: a
/// missing key renders as the key, and Dutch shipped with none of these, so the first Dutch sign-in
/// mail arrived with the subject "email.otp.subject".
/// </summary>
public class ServerTranslationTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "i18n");

    private static Dictionary<string, string> Read(string lang)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(
               File.ReadAllText(Path.Combine(Folder, $"{lang}.json")))!;

    public static TheoryData<Language> EveryLanguage()
    {
        var data = new TheoryData<Language>();
        foreach (var lang in Enum.GetValues<Language>()) data.Add(lang);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryLanguage))]
    public void Every_language_the_app_offers_can_write_its_own_emails(Language lang)
    {
        var code = lang switch { Language.Fa => "fa", Language.Nl => "nl", _ => "en" };
        var path = Path.Combine(Folder, $"{code}.json");

        Assert.True(File.Exists(path), $"{code}.json is missing: mail in {lang} would arrive as raw keys.");

        var english = Read("en");
        var table = Read(code);

        var missing = english.Keys.Except(table.Keys).ToList();
        Assert.True(missing.Count == 0, $"{code}.json is missing: {string.Join(", ", missing)}");

        Assert.All(table, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value),
            $"{code}.json has an empty value for {entry.Key}"));
    }

    /// <summary>A placeholder dropped in translation silently loses the code or the link itself.</summary>
    [Theory]
    [MemberData(nameof(EveryLanguage))]
    public void Placeholders_survive_translation(Language lang)
    {
        var code = lang switch { Language.Fa => "fa", Language.Nl => "nl", _ => "en" };
        var english = Read("en");
        var table = Read(code);

        foreach (var (key, source) in english)
        {
            var expected = Enumerable.Range(0, 3).Count(i => source.Contains($"{{{i}}}"));
            var actual = Enumerable.Range(0, 3).Count(i => table[key].Contains($"{{{i}}}"));

            Assert.True(expected == actual, $"{code}.json '{key}' has {actual} placeholders, English has {expected}");
        }
    }
}
