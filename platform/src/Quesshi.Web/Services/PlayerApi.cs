using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Web.Models;

namespace Quesshi.Web.Services;

public sealed class PlayerApi(ClientHttp client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? antiforgeryToken;

    public async Task<ApiResponse<PlayerProfile>> GetMeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/me"), cancellationToken);
        return await ReadAsync<PlayerProfile>(response, cancellationToken);
    }

    public async Task<ApiResponse<VerificationResult>> RequestCodeAsync(string email, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<VerificationResult>(HttpMethod.Post, "/api/auth/code", new { email }, cancellationToken);

    public async Task<ApiResponse<VerificationResult>> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<VerificationResult>(HttpMethod.Post, "/api/auth/verify", new { email, code }, cancellationToken);

    public async Task<ApiResponse<VerificationResult>> SignInWithGoogleAsync(string idToken, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<VerificationResult>(HttpMethod.Post, "/api/auth/x/google", new { idToken }, cancellationToken);

    public async Task<ApiResponse<VerificationResult>> ConfirmProfileAsync(string token, string displayName, string avatarSeed, string lang, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<VerificationResult>(HttpMethod.Post, "/api/auth/profile", new { token, displayName, avatarSeed, lang }, cancellationToken);

    public async Task<ApiResponse<PlayerProfile>> CreateGuestAsync(string displayName, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<PlayerProfile>(HttpMethod.Post, "/api/auth/guest", new { displayName }, cancellationToken);

    public async Task<ApiResponse<VerificationResult>> UpgradeGuestAsync(string email, string code, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<VerificationResult>(HttpMethod.Post, "/api/me/upgrade", new { email, code }, cancellationToken);

    public async Task<ApiResponse<PlayerProfile>> SaveProfileAsync(string displayName, string avatarSeed, string lang, CancellationToken cancellationToken = default) =>
        await SendMutationAsync<PlayerProfile>(HttpMethod.Put, "/api/me", new { displayName, avatarSeed, lang }, cancellationToken);

    public async Task<ApiResponse<object>> SignOutAsync(CancellationToken cancellationToken = default) =>
        await SendMutationAsync<object>(HttpMethod.Post, "/api/auth/sign-out", null, cancellationToken);

    public async Task<ApiResponse<object>> DeleteAccountAsync(CancellationToken cancellationToken = default) =>
        await SendMutationAsync<object>(HttpMethod.Delete, "/api/me", null, cancellationToken);

    public async Task RefreshAntiforgeryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<AntiforgeryDocument>(JsonOptions, cancellationToken);
        antiforgeryToken = token?.Token ?? throw new InvalidOperationException("The server returned an empty antiforgery token.");
    }

    private async Task<ApiResponse<T>> SendMutationAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (string.IsNullOrEmpty(antiforgeryToken)) await RefreshAntiforgeryAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("X-CSRF", antiforgeryToken);
            if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && attempt == 0)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                if (string.Equals(error, "invalid_antiforgery", StringComparison.Ordinal))
                {
                    await RefreshAntiforgeryAsync(cancellationToken);
                    continue;
                }

                return new ApiResponse<T>(response.StatusCode, default, error);
            }

            return await ReadAsync<T>(response, cancellationToken);
        }

        throw new InvalidOperationException("The antiforgery retry loop completed unexpectedly.");
    }

    private static async Task<ApiResponse<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = response.IsSuccessStatusCode ? null : await ReadErrorAsync(response, cancellationToken);
        var value = response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent
            ? await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            : default;
        return new ApiResponse<T>(response.StatusCode, value, error);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorDocument>(JsonOptions, cancellationToken);
            return error?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record AntiforgeryDocument(string? Token);
    private sealed record ErrorDocument(string? Error);
}

public sealed record ApiResponse<T>(HttpStatusCode StatusCode, T? Value, string? Error)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
}
