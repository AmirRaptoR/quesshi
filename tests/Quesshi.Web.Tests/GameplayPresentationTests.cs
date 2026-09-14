namespace Quesshi.Web.Tests;

public sealed class GameplayPresentationTests
{
    [Fact]
    public void Every_gameplay_page_uses_the_shared_game_shell_and_stage()
    {
        foreach (var page in new[] { "Play.razor", "Live.razor", "Matching.razor" })
        {
            var source = File.ReadAllText(Page(page));
            Assert.Contains("game-shell", source);
            Assert.Contains("game-stage", source);
            Assert.Contains("game-hud", source);
            Assert.Contains("game-progress", source);
        }
    }

    [Fact]
    public void Gameplay_css_has_motion_touch_and_reduced_motion_treatments()
    {
        var css = File.ReadAllText(Source("wwwroot/css/app.css"));

        Assert.Contains("@keyframes game-stage-in", css);
        Assert.Contains("@keyframes answer-deal", css);
        Assert.Contains(".game-answer", css);
        Assert.Contains("min-height: 3.75rem", css);
        Assert.Contains("prefers-reduced-motion: reduce", css);
    }

    [Fact]
    public void Active_games_hide_the_application_tab_bar()
    {
        var layout = File.ReadAllText(Source("Layout/MainLayout.razor"));

        Assert.Contains("\"/play/\"", layout);
        Assert.Contains("\"/live/\"", layout);
        Assert.Contains("\"/matching/\"", layout);
    }

    private static string Page(string name) => Source($"Pages/{name}");
    private static string Source(string relative) => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web", relative));
}
