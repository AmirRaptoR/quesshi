using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Which topics a duel is drawn from, as issue #89's Topics page works it: a page of large tiles
/// rather than a wrap of chips, with "Any topic" as one tap rather than as the absence of taps.
///
/// Pure, and separate from the page, for the same reason <see cref="HomeDuels"/> and
/// <see cref="LobbyPresentation"/> are: "which categories may this language even offer", "what does
/// tapping one do to the picks" and "what does the button say" are decisions, and decisions are the
/// half worth testing. The page is left holding markup.
///
/// Nothing picked is not "none" — it is "surprise me", and the server draws the topics itself. That
/// asymmetry is why <see cref="Any"/> exists as an action of its own instead of the player having to
/// untick their way back to it.
/// </summary>
public static class TopicPicks
{
    /// <summary>
    /// The topics this language can actually be played in — the same rule the chips it replaces
    /// followed, applied by hiding rather than by disabling. A disabled chip was worth showing in a
    /// wrap of twenty small ones, where its greyed-out label still told you the topic exists; a
    /// 132-pixel tile that cannot be tapped is a hole in the grid, and a page of them is a page
    /// mostly about what you cannot have. <c>Langs</c> null means the server did not say, which is
    /// treated as playable — the old picker's own reading of it.
    /// </summary>
    public static List<CategoryDto> Offered(IEnumerable<CategoryDto>? categories, string lang)
        => [.. (categories ?? []).Where(c => c.IsActive && (c.Langs is null || c.Langs.Contains(lang)))];

    /// <summary>
    /// One tile tapped: in if it was out, out if it was in. Order follows the catalogue rather than
    /// the taps (see <see cref="Chosen"/>), so this only has to decide membership.
    /// </summary>
    public static List<string> Toggle(IEnumerable<string>? picked, string id)
    {
        var next = new List<string>(picked ?? []);
        if (!next.Remove(id)) next.Add(id);
        return next;
    }

    /// <summary>"Any topic": the empty pick, which every create path already reads as "draw them
    /// yourself".</summary>
    public static List<string> Any() => [];

    /// <summary>
    /// The picks that survive a change of language. A player who picked three Persian-only topics
    /// and then switched the duel to Dutch would otherwise carry three invisible picks — a count in
    /// the summary line with no tiles behind it to explain or undo it, and a question set the server
    /// would refuse to fill. Dropping them silently is the honest reading of what the player did:
    /// they changed what the duel is, and those topics are not part of it any more.
    /// </summary>
    public static List<string> Prune(IEnumerable<string>? picked, IEnumerable<CategoryDto> offered)
    {
        var alive = offered.Select(c => c.Id).ToHashSet();
        return [.. (picked ?? []).Where(alive.Contains)];
    }

    /// <summary>
    /// The picked categories themselves, in the order the grid shows them rather than the order they
    /// were tapped in. The summary row reads "Geography, General, Science" — a list whose order
    /// changed every time somebody re-picked a topic would look like a different answer each time.
    /// </summary>
    public static List<CategoryDto> Chosen(IEnumerable<CategoryDto> offered, IEnumerable<string>? picked)
    {
        var wanted = (picked ?? []).ToHashSet();
        return [.. offered.Where(c => wanted.Contains(c.Id))];
    }
}

/// <summary>
/// The wording around a topic pick. Split from the decisions above exactly as
/// <see cref="DuelSettingsLine"/> is split from <see cref="DuelSettingsSummary"/>: this half needs
/// the real i18n tables, the other half needs nothing at all.
/// </summary>
public static class TopicText
{
    /// <summary>
    /// The Topics page's one button. It carries the count because the button is at the bottom of a
    /// grid that scrolls — by the time you reach it the tiles you ticked may be off-screen, and
    /// "Done · 3 picked" is the receipt for what you are about to keep. "Any topic" rather than
    /// "0 picked" for the empty case: zero topics is not what an empty pick means.
    /// </summary>
    public static string Done(int picked, Translator l)
        => picked == 0 ? l["topics.doneAny"] : l.Format("topics.donePicked", l.Num(picked));

    /// <summary>The names on the questions sheet's topics row, in the reader's own language and
    /// their stored form (issue #87: category names are never re-cased or shortened).</summary>
    public static string Names(IEnumerable<CategoryDto> chosen, Translator l)
        => string.Join(l["common.listSeparator"], chosen.Select(l.Name));

    /// <summary>"3 of 13 topics" — the count alone would not say how much of the bank three topics
    /// is, and the whole point of the row is to be readable without opening the page behind it.</summary>
    public static string Count(int picked, int offered, Translator l)
        => l.Format("questions.topicsOf", l.Num(picked), l.Num(offered));
}
