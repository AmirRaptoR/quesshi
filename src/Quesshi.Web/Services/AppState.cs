using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    public event Action? Changed;

    public async Task InitialiseAsync()
    {
        if (Ready) return;

        Lang = await js.InvokeAsync<string?>("quesshi.get", LangKey) ?? "fa";
        await translator.UseAsync(Lang);
        await js.InvokeVoidAsync("quesshi.setLang", Lang);

        GuestMatchId = await js.InvokeAsync<string?>("quesshi.get", GuestMatchKey);
        GuestMatchIsLive = await js.InvokeAsync<string?>("quesshi.get", GuestMatchLiveKey) == "1";

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
