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

    public string? Token { get; private set; }
    public MeDto? Me { get; private set; }
    public string Lang { get; private set; } = "fa";
    public bool Ready { get; private set; }

    public bool SignedIn => Token is not null && Me is not null;

    /// <summary>Signed in, but only for one duel. Everything else in the app is closed to them.</summary>
    public bool IsGuest => Me?.IsGuest == true;

    /// <summary>The one match a guest may look at. Null for everybody else.</summary>
    public string? GuestMatchId { get; private set; }

    public event Action? Changed;

    public async Task InitialiseAsync()
    {
        if (Ready) return;

        Lang = await js.InvokeAsync<string?>("quesshi.get", LangKey) ?? "fa";
        await translator.UseAsync(Lang);
        await js.InvokeVoidAsync("quesshi.setLang", Lang);

        GuestMatchId = await js.InvokeAsync<string?>("quesshi.get", GuestMatchKey);

        var token = await js.InvokeAsync<string?>("quesshi.get", TokenKey);
        if (!string.IsNullOrWhiteSpace(token))
        {
            Apply(token);
            try
            {
                Me = await http.GetFromJsonAsync<MeDto>("api/me");
                if (Me is not null) Lang = Me.Lang;
            }
            catch
            {
                // An expired or revoked token is not an error worth showing; just sign out quietly.
                await SignOutAsync();
            }
        }

        Ready = true;
        Changed?.Invoke();
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
    public async Task SignInAsGuestAsync(GuestResultDto result)
    {
        Apply(result.Token);
        Me = result.Me;
        GuestMatchId = result.Match.Id;

        await js.InvokeVoidAsync("quesshi.set", TokenKey, result.Token);
        await js.InvokeVoidAsync("quesshi.set", GuestMatchKey, result.Match.Id);
        await SetLangAsync(result.Me.Lang);
    }

    public async Task SignOutAsync()
    {
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
        await js.InvokeVoidAsync("quesshi.remove", GuestMatchKey);
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
