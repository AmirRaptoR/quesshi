using System.Text.Json;

namespace Quesshi.Server.Tests;

/// <summary>
/// Interim coverage for the browser i18n bundle at <c>Quesshi.Web/wwwroot/i18n/*.json</c>. There is
/// no dedicated browser translation test on <c>main</c> yet — #13's PR #20 (still a draft) is meant
/// to own that — so this only compares the three files' key sets, exactly what the live-queue issue
/// (#26) asks for until that PR lands. Delete this once PR #20's BrowserTranslationTests supersedes it.
/// </summary>
public class BrowserTranslationTests
{
    private static readonly string[] Langs = ["fa", "en", "nl"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Quesshi.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Dictionary<string, string> Read(string lang)
    {
        var path = Path.Combine(RepoRoot(), "src", "Quesshi.Web", "wwwroot", "i18n", $"{lang}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Fact]
    public void The_three_browser_bundles_carry_the_same_key_set()
    {
        var tables = Langs.ToDictionary(l => l, Read);
        var union = tables.Values.SelectMany(t => t.Keys).ToHashSet();

        foreach (var (lang, table) in tables)
        {
            var missing = union.Except(table.Keys).ToList();
            Assert.True(missing.Count == 0, $"{lang}.json is missing: {string.Join(", ", missing)}");
        }
    }
}
