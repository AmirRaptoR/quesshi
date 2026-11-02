using System.Text.Json;

namespace Quesshi.Web.Tests;

public sealed class LiveUnansweredSurfaceTests
{
    private static readonly string Source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));

    [Fact]
    public void The_live_screen_has_an_explicit_unanswered_surface()
    {
        var live = File.ReadAllText(Path.Combine(Source, "Quesshi.Web.Plugins/Formats/Live/LiveScreen.razor"));

        Assert.Contains("live.noAnswer", live);
        Assert.Contains("live.players.notEnoughAnswers", live);
    }

    [Fact]
    public void Unanswered_copy_exists_in_every_browser_language()
    {
        foreach (var lang in new[] { "en", "fa", "nl" })
        {
            var json = File.ReadAllText(Path.Combine(Source, $"Quesshi.Web/wwwroot/i18n/{lang}.json"));
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            Assert.True(table.ContainsKey("live.noAnswer"));
            Assert.True(table.ContainsKey("live.players.notEnoughAnswers"));
        }
    }
}
