using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;



/// <summary>Passwordless sign-in: a one-time email code, or Google. Both land on the same player record.</summary>
public sealed class AuthService(IPlayerRepository players, IOtpStore otps, IOtpSender sender, IClock clock, IIdFactory ids)
{
    /// <summary>Returns the code only when the sender is a dev stub, so the sign-in screen can show it locally.</summary>
    public async Task<string?> RequestOtpAsync(string email, Language lang, CancellationToken ct = default)
    {
        var code = OtpChallenge.NewCode();
        var challenge = OtpChallenge.Issue(email, code, clock.Now);
        await otps.SaveAsync(challenge, ct);
        return await sender.SendAsync(challenge.Email, code, lang, ct);
    }

    public async Task<SignInResult> VerifyOtpAsync(string email, string code, Language lang = Language.Fa, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        var challenge = await otps.GetAsync(normalized, ct);

        // An unknown address is indistinguishable from an expired one on purpose.
        if (challenge is null) return new SignInResult(OtpResult.Expired, null);

        var result = challenge.Verify(code, clock.Now);
        await otps.SaveAsync(challenge, ct); // attempts have to survive the round trip or the limit is fiction

        if (result != OtpResult.Ok) return new SignInResult(result, null);

        await otps.DeleteAsync(normalized, ct);
        var player = await GetOrCreateAsync(normalized, DefaultName(normalized), lang, ct);

        // A ban reads as a bad code: never confirm that the address exists.
        return player.IsBanned ? new SignInResult(OtpResult.Wrong, null) : new SignInResult(OtpResult.Ok, player);
    }

    public async Task<Player?> SignInWithGoogleAsync(string email, string? displayName, Language lang = Language.Fa, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        var player = await GetOrCreateAsync(normalized, string.IsNullOrWhiteSpace(displayName) ? DefaultName(normalized) : displayName!, lang, ct);
        return player.IsBanned ? null : player;
    }

    private async Task<Player> GetOrCreateAsync(string email, string displayName, Language lang, CancellationToken ct)
    {
        if (await players.GetByEmailAsync(email, ct) is { } existing) return existing;

        var created = Player.Register(ids.NewId(), email, displayName, lang, clock.Now);
        await players.UpsertAsync(created, ct);
        return created;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private static string DefaultName(string email)
    {
        var local = email.Split('@')[0];
        return string.IsNullOrWhiteSpace(local) ? "player" : local;
    }
}
