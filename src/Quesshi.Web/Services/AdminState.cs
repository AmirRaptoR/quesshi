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

    public string? Token { get; private set; }
    public AdminIdentityDto? Admin { get; private set; }
    public bool Ready { get; private set; }

    public bool SignedIn => Token is not null && Admin is not null;

    public event Action? Changed;

    public async Task InitialiseAsync()
    {
        if (Ready) return;

        var token = await js.InvokeAsync<string?>("quesshi.get", TokenKey);
        if (!string.IsNullOrWhiteSpace(token))
        {
            Apply(token);
            try
            {
                Admin = await http.Client.GetFromJsonAsync<AdminIdentityDto>("api/admin/auth/me");
            }
            catch
            {
                // An expired admin session is normal — they are short by design.
                await SignOutAsync();
            }
        }

        Ready = true;
        Changed?.Invoke();
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
