using System.Text.Json;

namespace Quesshi.Web.Tests;

public sealed class LiveUnansweredSurfaceTests
{
    private static readonly string Source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web"));

    [Fact]
    public void Every_reveal_kind_has_an_explicit_unanswered_surface()
    {
        var live = File.ReadAllText(Path.Combine(Source, "Pages/Live.razor"));
        var sort = File.ReadAllText(Path.Combine(Source, "Components/SortReveal.razor"));
        var map = File.ReadAllText(Path.Combine(Source, "Components/MapReveal.razor"));

        Assert.Contains("live.noAnswer", live);
        Assert.Contains("live.noAnswer", sort);
        Assert.Contains("live.noAnswer", map);
        Assert.Contains("live.players.notEnoughAnswers", live);
    }

    [Fact]
    public void Unanswered_copy_exists_in_every_browser_language()
    {
        foreach (var lang in new[] { "en", "fa", "nl" })
        {
            var json = File.ReadAllText(Path.Combine(Source, $"wwwroot/i18n/{lang}.json"));
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            Assert.True(table.ContainsKey("live.noAnswer"));
            Assert.True(table.ContainsKey("live.players.notEnoughAnswers"));
        }
    }
}
