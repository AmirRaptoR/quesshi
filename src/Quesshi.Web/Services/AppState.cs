using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>Who is signed in, in what language. Persisted to localStorage so a reload keeps you in.</summary>
public sealed class AppState(HttpClient http, IJSRuntime js, Translator translator)
{
    private const string TokenKey = "quesshi.token";
    private const string LangKey = "quesshi.lang";
    private const string GuestMatchKey = "quesshi.guestMatch";
    private const string GuestMatchLiveKey = "quesshi.guestMatchLive";
    private const string HomeTabKey = "quesshi.homeTab";
    private const string DuelSettingsKey = "quesshi.duelSettings";

    private int _generation;
    private bool _retrying;

    public string? Token { get; private set; }
    public MeDto? Me { get; private set; }
    public string Lang { get; private set; } = "fa";
    public bool Ready { get; private set; }

    public bool SignedIn => Token is not null && Me is not null;

    /// <summary>A token is held but the identity call hasn't confirmed it — a retry, not a sign-in wall.</summary>
    public bool Unconfirmed => Ready && Token is not null && Me is null;

    /// <summary>A retry is already in flight; offering a second one would race the first.</summary>
    public bool Retrying => _retrying;

    /// <summary>Signed in, but only for one duel. Everything else in the app is closed to them.</summary>
    public bool IsGuest => Me?.IsGuest == true;

    /// <summary>The one match a guest may look at. Null for everybody else.</summary>
    public string? GuestMatchId { get; private set; }

    /// <summary>Which kind of duel <see cref="GuestMatchId"/> is — a live lobby routes to <c>/live/{id}</c>,
    /// an async one to <c>/duel/{id}</c>. Meaningless while <see cref="GuestMatchId"/> is null.</summary>
    public bool GuestMatchIsLive { get; private set; }

    /// <summary>
    /// Which of the home's two tabs was last open. Persisted (issue #88) because Live and Offline are
    /// two different games rather than two views of one: a player who only ever plays turn-based duels
    /// should reopen the app on that list, not be handed the live lobby every time.
    /// </summary>
    public string HomeTab { get; private set; } = HomeTabs.Live;

    public async Task SetHomeTabAsync(string tab)
    {
        var next = HomeTabs.Normalise(tab);
        if (next == HomeTab) return;

        HomeTab = next;
        await js.InvokeVoidAsync("quesshi.set", HomeTabKey, next);
    }

    /// <summary>The language / question count / categories / levels last used to start a duel on
    /// <c>Home.razor</c> — what a friend challenge reuses. Null fields mean "no preference yet";
    /// the caller falls back to defaults.</summary>
    public string? LastLiveLang { get; private set; }
    public int? LastLiveQuestionCount { get; private set; }
    public List<string>? LastLiveCategories { get; private set; }
    public List<int>? LastLiveLevels { get; private set; }

    /// <summary>What the home creates a duel with when nobody has chosen anything yet — the first of
    /// <c>MatchRules.QuestionCountChoices</c>, the same value the old form opened on.</summary>
    public const int DefaultQuestionCount = 10;

    /// <summary>
    /// The four fields above as the one record the rest of the app already passes around, with the
    /// defaults filled in. Issue #88 asked for last-used settings to persist rather than for a second
    /// store beside this one, so this is a view over what was already here — <c>SetLastLiveSettings</c>
    /// keeps working untouched for the callers that only ever set it in memory.
    /// </summary>
    public DuelSettingsDto LastDuelSettings => new(
        LastLiveLang ?? Lang,
        LastLiveQuestionCount ?? DefaultQuestionCount,
        LastLiveCategories ?? [],
        LastLiveLevels ?? []);

    public void SetLastLiveSettings(string lang, int questionCount, List<string>? categories, List<int>? levels)
    {
        LastLiveLang = lang;
        LastLiveQuestionCount = questionCount;
        LastLiveCategories = categories;
        LastLiveLevels = levels;
    }

    /// <summary>
    /// The same setter, plus localStorage, so the choice outlives the tab it was made in. Empty lists
    /// are stored as the nulls the fields above use for "no preference": the distinction matters at
    /// the API, where an empty categories list means "draw them yourself" rather than "none".
    /// </summary>
    public async Task SetLastDuelSettingsAsync(DuelSettingsDto settings)
    {
        SetLastLiveSettings(settings.Lang, settings.QuestionCount,
            settings.CategoryIds.Count == 0 ? null : settings.CategoryIds,
            settings.Levels.Count == 0 ? null : settings.Levels);

        await js.InvokeVoidAsync("quesshi.set", DuelSettingsKey, JsonSerializer.Serialize(settings));
    }

    /// <summary>
    /// Reads back what <see cref="SetLastDuelSettingsAsync"/> wrote. Anything unreadable — a
    /// half-written value, or a shape from an older build — is ignored rather than allowed to take
    /// the app down on startup: a forgotten preference is a much smaller loss than a blank page.
    /// </summary>
    private async Task RestoreDuelSettingsAsync()
    {
        var stored = await js.InvokeAsync<string?>("quesshi.get", DuelSettingsKey);
        if (stored is not { Length: > 0 }) return;

        try
        {
            if (JsonSerializer.Deserialize<DuelSettingsDto>(stored) is { } settings)
                SetLastLiveSettings(settings.Lang, settings.QuestionCount,
                    settings.CategoryIds is { Count: > 0 } categories ? categories : null,
                    settings.Levels is { Count: > 0 } levels ? levels : null);
        }
        catch (JsonException)
        {
        }
    }

    public event Action? Changed;

    public async Task InitialiseAsync()
    {
        if (Ready) return;

        Lang = await js.InvokeAsync<string?>("quesshi.get", LangKey) ?? "fa";
        await translator.UseAsync(Lang);
        await js.InvokeVoidAsync("quesshi.setLang", Lang);

        GuestMatchId = await js.InvokeAsync<string?>("quesshi.get", GuestMatchKey);
        GuestMatchIsLive = await js.InvokeAsync<string?>("quesshi.get", GuestMatchLiveKey) == "1";

        HomeTab = HomeTabs.Normalise(await js.InvokeAsync<string?>("quesshi.get", HomeTabKey));
        await RestoreDuelSettingsAsync();

        var token = await js.InvokeAsync<string?>("quesshi.get", TokenKey);
        if (!string.IsNullOrWhiteSpace(token))
        {
            Apply(token);
            await CheckIdentityAsync();
        }

        Ready = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-runs the identity call for a token that was retained after an inconclusive failure. Separate
    /// from <see cref="InitialiseAsync"/>, which returns immediately once <see cref="Ready"/> is set.
    /// </summary>
    public async Task RetryIdentityAsync()
    {
        if (_retrying || Token is null) return;

        _retrying = true;
        Changed?.Invoke();
        try
        {
            await CheckIdentityAsync();
        }
        finally
        {
            _retrying = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The one place that decides what an identity call's outcome means for the token: only a 401
    /// discards it (see <see cref="IdentityCheck"/>). Everything else — including a successful call
    /// whose body is JSON <c>null</c> — leaves it in place and <see cref="Me"/> unset.
    /// </summary>
    private async Task CheckIdentityAsync()
    {
        var generation = _generation;
        MeDto? me;
        try
        {
            me = await http.GetFromJsonAsync<MeDto>("api/me");
        }
        catch (Exception ex)
        {
            if (generation != _generation) return; // signed out while this call was in flight
            if (IdentityCheck.Classify(ex) == IdentityOutcome.Discard) await SignOutAsync();
            return;
        }

        if (generation != _generation) return; // signed out while this call was in flight
        if (me is null) return; // identity unconfirmed, not an error — retain the token and retry later

        Me = me;
        Lang = me.Lang;
    }

    public async Task SignInAsync(AuthResultDto result)
    {
        Apply(result.Token);
        Me = result.Me;
        await js.InvokeVoidAsync("quesshi.set", TokenKey, result.Token);
        await ForgetGuestMatchAsync();
        await SetLangAsync(result.Me.Lang);
    }

    /// <summary>Signs in as a guest and pins them to the duel they were invited to.</summary>
    public async Task SignInAsGuestAsync(GuestResultDto result) => await SignInAsGuestCoreAsync(result.Token, result.Me, result.Match.Id, isLive: false);

    /// <summary>The live twin: signs in as a guest and pins them to the lobby they were invited to.</summary>
    public async Task SignInAsGuestLiveAsync(GuestLiveResultDto result) => await SignInAsGuestCoreAsync(result.Token, result.Me, result.Live.Id, isLive: true);

    private async Task SignInAsGuestCoreAsync(string token, MeDto me, string matchId, bool isLive)
    {
        Apply(token);
        Me = me;
        GuestMatchId = matchId;
        GuestMatchIsLive = isLive;

        await js.InvokeVoidAsync("quesshi.set", TokenKey, token);
        await js.InvokeVoidAsync("quesshi.set", GuestMatchKey, matchId);
        await js.InvokeVoidAsync("quesshi.set", GuestMatchLiveKey, isLive ? "1" : "0");
        await SetLangAsync(me.Lang);
    }

    public async Task SignOutAsync()
    {
        _generation++; // orphans any identity check still in flight; it must not resurrect this session
        Token = null;
        Me = null;
        http.DefaultRequestHeaders.Authorization = null;
        await js.InvokeVoidAsync("quesshi.remove", TokenKey);
        await ForgetGuestMatchAsync();
        Changed?.Invoke();
    }

    private async Task ForgetGuestMatchAsync()
    {
        GuestMatchId = null;
        GuestMatchIsLive = false;
        await js.InvokeVoidAsync("quesshi.remove", GuestMatchKey);
        await js.InvokeVoidAsync("quesshi.remove", GuestMatchLiveKey);
    }

    /// <summary>
    /// Clears the pin once the duel it points at has finished — a result seen, a forfeit, an
    /// abandonment, or a live duel reaching phase <c>over</c> (issue #102). A no-op unless
    /// <paramref name="matchId"/> is still the one pinned: a stale tab finishing duel A must not erase
    /// a pin that has since moved on to duel B. Leaves the guest token alone — a finished duel's own
    /// page, and the "keep your score" sign-in link on it, both still need it.
    /// </summary>
    public async Task ForgetFinishedGuestMatchAsync(string matchId)
    {
        if (GuestMatchId != matchId) return;

        await ForgetGuestMatchAsync();
        Changed?.Invoke();
    }

    public void SetMe(MeDto me)
    {
        Me = me;
        Changed?.Invoke();
    }

    public async Task SetLangAsync(string lang)
    {
        Lang = lang;
        await translator.UseAsync(lang);
        await js.InvokeVoidAsync("quesshi.set", LangKey, lang);
        await js.InvokeVoidAsync("quesshi.setLang", lang);
        Changed?.Invoke();
    }

    private void Apply(string token)
    {
        Token = token;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
