using System.Security.Cryptography;
using System.Text;

namespace Quesshi.Domain;

/// <summary>
/// A single-use, short-lived permission to set a new password. The secret is never stored: only a
/// hash of it, so a leak of the store does not hand out password resets.
/// </summary>
public sealed class PasswordResetToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private PasswordResetToken(string adminUserId, string secretHash, DateTimeOffset issuedAt)
    {
        AdminUserId = adminUserId;
        SecretHash = secretHash;
        IssuedAt = issuedAt;
    }

    public string AdminUserId { get; }
    public string SecretHash { get; }
    public DateTimeOffset IssuedAt { get; }
    public bool Used { get; private set; }

    public DateTimeOffset ExpiresAt => IssuedAt + Lifetime;

    public static PasswordResetToken Issue(string adminUserId, string secret, DateTimeOffset now)
        => new(adminUserId, Hash(secret), now);

    public static PasswordResetToken Restore(string adminUserId, string secretHash, DateTimeOffset issuedAt, bool used)
        => new(adminUserId, secretHash, issuedAt) { Used = used };

    /// <summary>256 bits of randomness, url-safe so it can live in a reset link.</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>Is this token good? Changes nothing, so a caller can validate before committing to spend it.</summary>
    public ResetResult Check(string secret, DateTimeOffset now)
    {
        if (Used) return ResetResult.AlreadyUsed;
        if (now > ExpiresAt) return ResetResult.Expired;

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(SecretHash),
            Encoding.UTF8.GetBytes(Hash(secret ?? string.Empty)));

        return ok ? ResetResult.Ok : ResetResult.Wrong;
    }

    /// <summary>Spends the token. Only a successful redemption consumes it.</summary>
    public ResetResult Redeem(string secret, DateTimeOffset now)
    {
        var outcome = Check(secret, now);
        if (outcome == ResetResult.Ok) Used = true;
        return outcome;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
