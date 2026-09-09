using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// A duel's settings while they are being edited, plus where the player came from — the state issue
/// #89's two screens hand back and forth. It lives in the address bar rather than in
/// <see cref="AppState"/> on purpose, and the reason is worth stating: the questions sheet does not
/// commit anything until Done is pressed, but picking topics is a whole page away, so an in-memory
/// draft would have to be a second, half-committed copy of the settings sitting beside the real one
/// — visible to the home's summary line, indistinguishable from a saved choice, and gone on reload.
/// As a query string it is none of those things: the topics page is opened with a link, comes back
/// with a link, survives a reload or a shared URL, and until Done nothing anywhere has changed.
///
/// The parameter names are short because they end up in a URL a player can see (<c>?lang=en&amp;n=30</c>),
/// and unreadable values simply fall back — a hand-edited or half-written URL should open the sheet
/// on the last saved settings, never break it.
/// </summary>
/// <param name="Return">Where Done goes back to: the home, or the lobby whose owner opened this.</param>
/// <param name="LobbyId">
/// Set only when the sheet was opened from a lobby the player owns. The home's settings are a
/// preference — they decide what the *next* duel is drawn with — but a lobby is a room that already
/// exists with settings of its own, and its owner pressing Done on this sheet plainly means "change
/// this lobby", not "remember that for next time". So Done also PUTs them, through the endpoint the
/// old inline picker on that page already used (<see cref="Api.UpdateLobbySettingsAsync"/>), and
/// nothing new is needed server-side.
/// </param>
public sealed record QuestionsDraft(string Lang, int QuestionCount, DifficultyRange Levels,
    IReadOnlyList<string> Topics, string Return, string? LobbyId = null, bool LobbyIsLive = false)
{
    /// <summary>Where a player who arrived without saying comes from — the home, which is where all
    /// but one of the entry points into this sheet actually are.</summary>
    public const string Home = "/play";

    public static QuestionsDraft From(DuelSettingsDto settings, string? returnTo = null,
        string? lobbyId = null, bool lobbyIsLive = false) => new(
        settings.Lang, settings.QuestionCount, DifficultyRange.From(settings.Levels),
        settings.CategoryIds ?? [], SafeReturn(returnTo), lobbyId, lobbyIsLive);

    /// <summary>
    /// The draft carried by a URL, with <paramref name="saved"/> filling in every field the URL does
    /// not carry. That fallback is what makes the first arrival from the home — a bare
    /// <c>/play/questions</c> — open on the last-used settings rather than on nothing.
    /// </summary>
    public static QuestionsDraft Read(string uri, DuelSettingsDto saved)
    {
        var fallback = From(saved);

        var lang = QueryValues.Read(uri, "lang") is { Length: > 0 } l && Translator.All.Contains(l) ? l : fallback.Lang;
        var count = int.TryParse(QueryValues.Read(uri, "n"), out var n) && n > 0 ? n : fallback.QuestionCount;

        // Both ends or neither: a URL carrying only one of them says nothing about what the other
        // was, and guessing would silently widen or narrow somebody's choice.
        var levels = int.TryParse(QueryValues.Read(uri, "lo"), out var low)
                     && int.TryParse(QueryValues.Read(uri, "hi"), out var high)
            ? DifficultyRange.Whole.WithHigh(high).WithLow(low)
            : fallback.Levels;

        // A topics parameter that is present but empty is "Any topic", which is a real answer and
        // not the same as not having been asked — hence the null check rather than a length one.
        var topics = QueryValues.Read(uri, "t") is { } t
            ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : fallback.Topics;

        return new QuestionsDraft(lang, count, levels, topics, SafeReturn(QueryValues.Read(uri, "r")),
            QueryValues.Read(uri, "lb") is { Length: > 0 } lobby ? lobby : null,
            QueryValues.Read(uri, "lv") == "1");
    }

    /// <summary>This draft as a link to <paramref name="path"/> — how each of the two pages opens the
    /// other without either of them owning the state.</summary>
    public string LinkTo(string path)
    {
        var query = string.Join('&', new[]
        {
            $"lang={Uri.EscapeDataString(Lang)}",
            $"n={QuestionCount}",
            $"lo={Levels.Low}",
            $"hi={Levels.High}",
            $"t={Uri.EscapeDataString(string.Join(',', Topics))}",
            $"r={Uri.EscapeDataString(Return)}",
            LobbyId is { Length: > 0 } lobby ? $"lb={Uri.EscapeDataString(lobby)}&lv={(LobbyIsLive ? 1 : 0)}" : null
        }.OfType<string>());

        return $"{path}?{query}";
    }

    /// <summary>What Done saves: the same <c>DuelSettingsDto</c> every create path already takes,
    /// with the range flattened back to the level list the API speaks.</summary>
    public DuelSettingsDto ToSettings() => new(Lang, QuestionCount, [.. Topics], Levels.Levels());

    /// <summary>
    /// Where the back chevron and Done are allowed to send somebody. The value arrives from a URL,
    /// so it is treated as one: only a path within this app is honoured. A scheme-relative
    /// <c>//evil.example</c> is a full URL wearing a path's clothes and is the one that matters
    /// here — <c>NavigateTo</c> would happily leave the app for it, turning the sheet into an open
    /// redirect anybody could put in front of a player.
    /// </summary>
    public static string SafeReturn(string? value)
        => value is { Length: > 0 } path
           && path[0] == '/'
           && !path.StartsWith("//", StringComparison.Ordinal)
           && !path.StartsWith("/\\", StringComparison.Ordinal)
            ? path
            : Home;
}
