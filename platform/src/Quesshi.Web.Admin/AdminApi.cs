using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Quesshi.Web.Services;

namespace Quesshi.Web.Admin;

public sealed class AdminApi(ClientHttp client)
{
    private string? antiforgeryToken;

    public async Task<AdminResult<AdminSession>> CurrentAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/admin/auth/me", null, false, cancellationToken);
        return await ReadAsync<AdminSession>(response, cancellationToken);
    }

    public async Task<AdminResult<LoginResult>> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var result = await SendMutationAsync(HttpMethod.Post, "/api/admin/auth/login", new LoginRequest(username, password), cancellationToken);
        using (result.Response)
        {
            if (result.Success) await RefreshAntiforgeryAsync(cancellationToken);
            return await ReadAsync<LoginResult>(result.Response, cancellationToken);
        }
    }

    public async Task<AdminResult<object>> ForgotAsync(string usernameOrEmail, CancellationToken cancellationToken = default) =>
        await ReadMutationAsync(HttpMethod.Post, "/api/admin/auth/forgot", new ForgotRequest(usernameOrEmail), cancellationToken);

    public async Task<AdminResult<object>> ResetAsync(string token, string password, CancellationToken cancellationToken = default) =>
        await ReadMutationAsync(HttpMethod.Post, "/api/admin/auth/reset", new ResetRequest(token, password), cancellationToken);

    public async Task<AdminResult<object>> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        await ReadMutationAsync(HttpMethod.Post, "/api/admin/auth/password", new PasswordRequest(currentPassword, newPassword), cancellationToken);

    public async Task<AdminResult<object>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadMutationAsync(HttpMethod.Post, "/api/admin/auth/sign-out", null, cancellationToken);
        if (result.Success) await RefreshAntiforgeryAsync(cancellationToken);
        return result;
    }

    public async Task<AdminResult<IReadOnlyList<AdminAccount>>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/admin/accounts", null, false, cancellationToken);
        return await ReadAsync<IReadOnlyList<AdminAccount>>(response, cancellationToken);
    }

    public Task<AdminResult<AdminAccount>> CreateAsync(string username, string email, string password, CancellationToken cancellationToken = default) =>
        ReadMutationAsync<AdminAccount>(HttpMethod.Post, "/api/admin/accounts", new CreateRequest(username, email, password), cancellationToken);

    public Task<AdminResult<object>> SetMembershipActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) =>
        ReadMutationAsync(HttpMethod.Post, $"/api/admin/accounts/{id}/membership/active?value={active.ToString().ToLowerInvariant()}", null, cancellationToken);

    private async Task<AdminResult<T>> ReadMutationAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var result = await SendMutationAsync(method, path, body, cancellationToken);
        using (result.Response) return await ReadAsync<T>(result.Response, cancellationToken);
    }

    private async Task<AdminResult<object>> ReadMutationAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var result = await SendMutationAsync(method, path, body, cancellationToken);
        using (result.Response) return await ReadAsync<object>(result.Response, cancellationToken);
    }

    private async Task<(HttpResponseMessage Response, bool Success)> SendMutationAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (antiforgeryToken is null) await RefreshAntiforgeryAsync(cancellationToken);
        var response = await SendAsync(method, path, body, true, cancellationToken);
        if (await IsStaleTokenAsync(response, cancellationToken))
        {
            response.Dispose();
            await RefreshAntiforgeryAsync(cancellationToken);
            response = await SendAsync(method, path, body, true, cancellationToken);
        }
        return (response, response.IsSuccessStatusCode);
    }

    private async Task RefreshAntiforgeryAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/admin/antiforgery", null, false, cancellationToken, uncached: true);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<AntiforgeryToken>(cancellationToken: cancellationToken);
        antiforgeryToken = token?.Token ?? throw new InvalidOperationException("The server did not return an admin antiforgery token.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool includeToken, CancellationToken cancellationToken, bool uncached = false)
    {
        var request = new HttpRequestMessage(method, path);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        if (uncached) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (body is not null) request.Content = JsonContent.Create(body);
        if (includeToken) request.Headers.TryAddWithoutValidation("X-CSRF-Admin", antiforgeryToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<bool> IsStaleTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest) return false;
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var error = JsonSerializer.Deserialize<ErrorResponse>(payload, JsonSerializerOptions.Web);
        return string.Equals(error?.Error, "invalid_antiforgery", StringComparison.Ordinal);
    }

    private static async Task<AdminResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return new(true, response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken), null);
        ErrorResponse? error = null;
        if (response.Content.Headers.ContentLength is not 0)
        {
            try { error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: cancellationToken); }
            catch (System.Text.Json.JsonException) { }
        }
        return new(false, default, LocalizedError(error?.Code ?? error?.Error, response.StatusCode));
    }

    private static string LocalizedError(string? error, HttpStatusCode statusCode) => error switch
    {
        null when statusCode == HttpStatusCode.Unauthorized => "admin.auth.signedOut",
        null => "admin.error",
        "last_owner" => "admin.accounts.lastOwner",
        "invalid_role" or "last_platform_admin" or "account_change_failed" or "invalid_antiforgery" => "admin.error",
        _ when error.StartsWith("admin.", StringComparison.Ordinal) => error,
        _ => "admin.error",
    };

    private sealed record AntiforgeryToken(string? Token);
    private sealed record ErrorResponse(string? Code, string? Error);
    private sealed record LoginRequest(string Username, string Password);
    private sealed record ForgotRequest(string UsernameOrEmail);
    private sealed record ResetRequest(string Token, string NewPassword);
    private sealed record PasswordRequest(string CurrentPassword, string NewPassword);
    private sealed record CreateRequest(string Username, string Email, string Password);
}

public sealed record AdminResult<T>(bool Success, T? Value, string? Error);
public sealed record LoginResult(bool MustChangePassword);
public sealed record AdminSession(Guid Id, string? Role, bool IsPlatform, bool MustChangePassword)
{
    public bool CanManageAccounts => IsPlatform || string.Equals(Role, "Owner", StringComparison.OrdinalIgnoreCase);
}
public sealed record AdminAccount(Guid Id, string Username, string Email, bool IsActive, bool IsMembershipActive, bool IsPlatform, bool MustChangePassword, string Role);
