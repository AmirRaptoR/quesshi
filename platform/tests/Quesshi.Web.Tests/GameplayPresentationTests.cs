namespace Quesshi.Web.Tests;

public sealed class GameplayPresentationTests
{
    [Fact]
    public void The_async_duel_screen_uses_the_shared_game_shell_stage_and_hud()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src/Quesshi.Web.Plugins/Formats/AsyncDuel/AsyncDuelScreen.razor"));

        Assert.Contains("game-shell", source);
        Assert.Contains("game-stage", source);
        Assert.Contains("game-hud", source);
    }

    [Fact]
    public void Gameplay_css_has_motion_touch_and_reduced_motion_treatments()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/Quesshi.Web/wwwroot/css/app.css"));

        Assert.Contains("@keyframes game-stage-in", css);
        Assert.Contains("@keyframes answer-deal", css);
        Assert.Contains(".game-answer", css);
        Assert.Contains("min-height: 3.75rem", css);
        Assert.Contains("prefers-reduced-motion: reduce", css);
    }

    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
