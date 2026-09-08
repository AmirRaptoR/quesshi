using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;



/// <summary>Passwordless sign-in: a one-time email code, or Google. Both land on the same player record.</summary>
public sealed class AuthService(IPlayerRepository players, IOtpStore otps, IOtpSender sender, IClock clock, IIdFactory ids)
{
    /// <summary>Null: the code reaches the player by mail or by log, never as the answer to this call.</summary>
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

    /// <summary>
    /// The guest upgrade's own verify path (issue #55, spec section 4). Deliberately not
    /// <see cref="VerifyOtpAsync"/>, which ends in <see cref="GetOrCreateAsync"/> and would register a
    /// brand-new player id for <paramref name="email"/> — creating exactly the second account the
    /// upgrade exists to avoid, and stranding the caller's existing guest id beside it. This method
    /// validates the challenge exactly the same way <see cref="VerifyOtpAsync"/> does — the same
    /// attempt-count save that must survive the round trip whether the attempt is right or wrong, and
    /// the same delete on success — but stops there: it never fetches or creates a <see cref="Player"/>.
    /// Claiming <paramref name="email"/> onto the caller's own id is <c>IPlayerGrain.ClaimEmailAsync</c>'s
    /// job, because every mutation of a <see cref="Player"/> goes through that grain now, this one
    /// included.
    ///
    /// The address-taken check runs only after the challenge itself verifies, so a wrong or expired
    /// code never leaks whether some other address already has an account — the same "an unknown or
    /// banned address reads as a bad code" discipline <see cref="VerifyOtpAsync"/> already follows.
    /// Checked explicitly here rather than left entirely to Mongo's unique index on <c>Email</c>, so a
    /// taken address is refused with a clear, translatable reason instead of a raw write conflict; the
    /// index remains the actual backstop for the race between this check and the grain's later write.
    /// </summary>
    public async Task<UpgradeVerifyResult> VerifyUpgradeAsync(string email, string code, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        var challenge = await otps.GetAsync(normalized, ct);

        // An unknown address is indistinguishable from an expired one, same as VerifyOtpAsync.
        if (challenge is null) return new UpgradeVerifyResult(OtpResult.Expired, null);

        var result = challenge.Verify(code, clock.Now);
        await otps.SaveAsync(challenge, ct); // attempts have to survive the round trip or the limit is fiction

        if (result != OtpResult.Ok) return new UpgradeVerifyResult(result, null);

        await otps.DeleteAsync(normalized, ct);

        return await players.GetByEmailAsync(normalized, ct) is not null
            ? new UpgradeVerifyResult(OtpResult.AddressTaken, null)
            : new UpgradeVerifyResult(OtpResult.Ok, normalized);
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
