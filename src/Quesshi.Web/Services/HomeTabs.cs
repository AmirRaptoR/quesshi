namespace Quesshi.Web.Services;

/// <summary>
/// The home's two tabs (issue #88), named once so the page, <see cref="AppState"/> and the
/// localStorage key all agree on the same two strings. Live and Offline are two different games —
/// one clock shared by everybody, versus each player answering when they are free — not a toggle on
/// one form, which is why the choice is remembered rather than reset to a default on every open: a
/// player who lives in one of them should not have to re-pick it every time the app starts.
/// </summary>
public static class HomeTabs
{
    public const string Live = "live";
    public const string Offline = "offline";

    /// <summary>
    /// Whatever came back out of localStorage, reduced to one of the two tabs that exist. Anything
    /// unrecognised — never written, hand-edited, or left behind by an older build — falls back to
    /// Live rather than throwing or rendering an empty page: a stored value is a preference, and a
    /// preference nobody can read is simply absent.
    /// </summary>
    public static string Normalise(string? stored) => stored == Offline ? Offline : Live;
}
