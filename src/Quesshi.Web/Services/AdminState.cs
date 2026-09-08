using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.JSInterop;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// The administrator session, kept entirely apart from <see cref="AppState"/>: a different token,
/// a different storage key, and a different HttpClient. Signing out of the game does not sign you
/// out of administration, and neither one can borrow the other's credentials.
/// </summary>
public sealed class AdminState(AdminHttpClient http, IJSRuntime js)
{
    private const string TokenKey = "quesshi.admin.token";

    private int _generation;
    private bool _retrying;

    public string? Token { get; private set; }
    public AdminIdentityDto? Admin { get; private set; }
    public bool Ready { get; private set; }

    public bool SignedIn => Token is not null && Admin is not null;

    /// <summary>A token is held but the identity call hasn't confirmed it — a retry, not a sign-in wall.</summary>
    public bool Unconfirmed => Ready && Token is not null && Admin is null;

    /// <summary>A retry is already in flight; offering a second one would race the first.</summary>
    public bool Retrying => _retrying;

    public event Action? Changed;

    public async Task InitialiseAsync()
    {
        if (Ready) return;

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
    /// whose body is JSON <c>null</c> — leaves it in place and <see cref="Admin"/> unset.
    /// </summary>
    private async Task CheckIdentityAsync()
    {
        var generation = _generation;
        AdminIdentityDto? admin;
        try
        {
            admin = await http.Client.GetFromJsonAsync<AdminIdentityDto>("api/admin/auth/me");
        }
        catch (Exception ex)
        {
            if (generation != _generation) return; // signed out while this call was in flight
            if (IdentityCheck.Classify(ex) == IdentityOutcome.Discard) await SignOutAsync();
            return;
        }

        if (generation != _generation) return; // signed out while this call was in flight
        if (admin is null) return; // identity unconfirmed, not an error — retain the token and retry later

        Admin = admin;
    }

    public async Task SignInAsync(AdminSessionDto session)
    {
        Apply(session.Token);
        Admin = session.Admin;
        await js.InvokeVoidAsync("quesshi.set", TokenKey, session.Token);
        Changed?.Invoke();
    }

    public void SetAdmin(AdminIdentityDto admin)
    {
        Admin = admin;
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        _generation++; // orphans any identity check still in flight; it must not resurrect this session
        Token = null;
        Admin = null;
        http.Client.DefaultRequestHeaders.Authorization = null;
        await js.InvokeVoidAsync("quesshi.remove", TokenKey);
        Changed?.Invoke();
    }

    private void Apply(string token)
    {
        Token = token;
        http.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
