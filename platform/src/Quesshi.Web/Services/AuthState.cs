using System.Net;
using Quesshi.Web.Models;

namespace Quesshi.Web.Services;

public enum PlayerIdentityStatus
{
    Loading,
    SignedOut,
    SignedIn,
    Unconfirmed,
}

public sealed class AuthState(PlayerApi api)
{
    public event Action? Changed;

    public PlayerIdentityStatus Status { get; private set; } = PlayerIdentityStatus.Loading;
    public PlayerProfile? Player { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await api.GetMeAsync(cancellationToken);
            if (result.IsSuccess && result.Value is not null)
            {
                Set(PlayerIdentityStatus.SignedIn, result.Value);
                return;
            }

            Set(result.StatusCode == HttpStatusCode.Unauthorized ? PlayerIdentityStatus.SignedOut : PlayerIdentityStatus.Unconfirmed, null);
        }
        catch (HttpRequestException)
        {
            Set(PlayerIdentityStatus.Unconfirmed, null);
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default) => InitializeAsync(cancellationToken);

    public Task<ApiResponse<VerificationResult>> RequestCodeAsync(string email, CancellationToken cancellationToken = default) =>
        api.RequestCodeAsync(email, cancellationToken);

    public async Task<ApiResponse<VerificationResult>> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var result = await api.VerifyCodeAsync(email, code, cancellationToken);
        if (result.IsSuccess && string.Equals(result.Value?.Status, "signed_in", StringComparison.Ordinal)) await ReloadAfterIdentityChangeAsync(cancellationToken);
        return result;
    }

    public async Task<ApiResponse<VerificationResult>> SignInWithGoogleAsync(string idToken, CancellationToken cancellationToken = default)
    {
        var result = await api.SignInWithGoogleAsync(idToken, cancellationToken);
        if (result.IsSuccess && string.Equals(result.Value?.Status, "signed_in", StringComparison.Ordinal)) await ReloadAfterIdentityChangeAsync(cancellationToken);
        return result;
    }

    public async Task<ApiResponse<VerificationResult>> ConfirmProfileAsync(string token, string displayName, string avatarSeed, string lang, CancellationToken cancellationToken = default)
    {
        var result = await api.ConfirmProfileAsync(token, displayName, avatarSeed, lang, cancellationToken);
        if (result.IsSuccess) await ReloadAfterIdentityChangeAsync(cancellationToken);
        return result;
    }

    public async Task<ApiResponse<PlayerProfile>> CreateGuestAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var result = await api.CreateGuestAsync(displayName, cancellationToken);
        if (result.IsSuccess && result.Value is not null)
        {
            await api.RefreshAntiforgeryAsync(cancellationToken);
            Set(PlayerIdentityStatus.SignedIn, result.Value with { IsGuest = true });
        }

        return result;
    }

    public async Task<ApiResponse<VerificationResult>> UpgradeGuestAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var result = await api.UpgradeGuestAsync(email, code, cancellationToken);
        if (result.IsSuccess) await ReloadAfterIdentityChangeAsync(cancellationToken);
        return result;
    }

    public async Task<ApiResponse<PlayerProfile>> SaveProfileAsync(string displayName, string avatarSeed, string lang, CancellationToken cancellationToken = default)
    {
        var result = await api.SaveProfileAsync(displayName, avatarSeed, lang, cancellationToken);
        if (result.IsSuccess && result.Value is not null) Set(PlayerIdentityStatus.SignedIn, result.Value with { IsGuest = Player?.IsGuest ?? false });
        return result;
    }

    public async Task<ApiResponse<object>> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var result = await api.SignOutAsync(cancellationToken);
        if (result.IsSuccess)
        {
            await api.RefreshAntiforgeryAsync(cancellationToken);
            Set(PlayerIdentityStatus.SignedOut, null);
        }

        return result;
    }

    public async Task<ApiResponse<object>> DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        var result = await api.DeleteAccountAsync(cancellationToken);
        if (result.IsSuccess)
        {
            await api.RefreshAntiforgeryAsync(cancellationToken);
            Set(PlayerIdentityStatus.SignedOut, null);
        }

        return result;
    }

    private async Task ReloadAfterIdentityChangeAsync(CancellationToken cancellationToken)
    {
        await api.RefreshAntiforgeryAsync(cancellationToken);
        await InitializeAsync(cancellationToken);
    }

    private void Set(PlayerIdentityStatus status, PlayerProfile? player)
    {
        Status = status;
        Player = player;
        Changed?.Invoke();
    }
}
