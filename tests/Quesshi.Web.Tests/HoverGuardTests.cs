using System.Text.RegularExpressions;

namespace Quesshi.Web.Tests;

/// <summary>
/// iOS Safari keeps <c>:hover</c> applied at the last tap point and re-applies it after layout, so an
/// unguarded hover rule reads as a stuck selection once the tiles it styled are re-enabled — see #103.
/// Every <c>:hover</c> selector in <c>app.css</c> has to sit behind
/// <c>@media (hover: hover) and (pointer: fine)</c>, which only matches a pointer capable of hovering,
/// so this walks the stylesheet by brace depth and fails on any that does not. It reads the real,
/// shipped file — via <see cref="RepoRoot"/>, the same walk-up <c>BrowserTranslationTests</c> in
/// <c>Quesshi.Server.Tests</c> uses — rather than a fixture that could agree with the scanner and
/// disagree with the asset.
/// </summary>
public class HoverGuardTests
{
    private const string GuardPrelude = "@media (hover: hover) and (pointer: fine)";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Quesshi.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string AppCss()
    {
        var path = Path.Combine(RepoRoot(), "src", "Quesshi.Web", "wwwroot", "css", "app.css");
        return File.ReadAllText(path);
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", "");

    /// <summary>
    /// Walks the stylesheet one character at a time, tracking brace depth. Each time a block opens,
    /// the text since the previous brace is that block's prelude — a selector for a ruleset, or an
    /// at-rule like <c>@media (...)</c> for a block that can itself hold rulesets. A prelude naming
    /// <c>:hover</c> is only acceptable if some enclosing prelude on the stack is, whitespace
    /// aside, exactly <see cref="GuardPrelude"/> — a differently-worded but equivalent guard does not
    /// count, which is deliberate: the exact form is the convention this test establishes.
    /// </summary>
    private static List<string> UnguardedHoverSelectors(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

        var stack = new List<string>();
        var buf = new System.Text.StringBuilder();
        var offenders = new List<string>();

        foreach (var ch in css)
        {
            if (ch == '{')
            {
                var prelude = buf.ToString().Trim();
                buf.Clear();
                if (prelude.Contains(":hover") && !stack.Any(p => Squash(p) == Squash(GuardPrelude)))
                {
                    offenders.Add(prelude);
                }
                stack.Add(prelude);
            }
            else if (ch == '}')
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                buf.Clear();
            }
            else
            {
                buf.Append(ch);
            }
        }

        return offenders;
    }

    [Fact]
    public void No_hover_selector_sits_outside_the_hover_capable_pointer_guard()
    {
        var offenders = UnguardedHoverSelectors(AppCss());

        Assert.True(offenders.Count == 0,
            "Unguarded :hover rule(s) outside `@media (hover: hover) and (pointer: fine)`:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void Locked_answer_fills_the_key_chip_so_hover_cannot_imitate_it()
    {
        // .answer:hover and .answer--locked read as the same turquoise a hair apart in alpha — the
        // bug this issue fixes. The key chip is untouched by hover, so filling it in is a mark a
        // stuck hover cannot reproduce, the same way .answer--right and .answer--wrong already do.
        var match = Regex.Match(AppCss(), @"\.answer--locked\s+\.answer__key\s*\{([^}]*)\}");

        Assert.True(match.Success, ".answer--locked .answer__key has no rule in app.css.");
        Assert.Contains("color:", match.Groups[1].Value);
        Assert.Contains("background:", match.Groups[1].Value);
    }
}
