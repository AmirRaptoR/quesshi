namespace Quesshi.Web.Services;

/// <summary>
/// The three language tiles, which are now asked in two places: Settings (issue #91, the language
/// you read the app in) and the questions sheet (issue #89, the language the next duel is drawn in).
/// The markup and the CSS are shared already; this is the one piece of the look that is a decision
/// rather than a class name, and it lives here so the two screens cannot drift into disagreeing
/// about how the same three words are set.
/// </summary>
public static class ScriptTile
{
    /// <summary>
    /// Persian reads at a comfortable size a couple of points larger than the Latin scripts, and
    /// "Nederlands" is the longest of the three words — matching the mockup's own per-tile sizing
    /// (فارسی 20px, English 18px, Nederlands 16px) keeps all three filling their tile evenly rather
    /// than one looking cramped and another swimming in space.
    /// </summary>
    public static string FontSize(string lang) => lang switch
    {
        "fa" => "1.25rem",
        "en" => "1.1rem",
        _ => "1rem"
    };
}
