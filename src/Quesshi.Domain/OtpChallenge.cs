using System.Security.Cryptography;
using System.Text;

namespace Quesshi.Domain;

/// <summary>A one-time login code. No passwords anywhere in Quesshi, so this is a trust boundary.</summary>
public sealed class OtpChallenge
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;
    public const int CodeLength = 6;

    private OtpChallenge(string email, string code, DateTimeOffset issuedAt)
    {
        Email = email;
        Code = code;
        IssuedAt = issuedAt;
    }

    public string Email { get; }
    public string Code { get; }
    public DateTimeOffset IssuedAt { get; }
    public int Attempts { get; private set; }
    public bool Used { get; private set; }

    public DateTimeOffset ExpiresAt => IssuedAt + Lifetime;

    public static OtpChallenge Issue(string email, string code, DateTimeOffset now) => new(email.Trim().ToLowerInvariant(), code, now);

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public OtpResult Verify(string attempt, DateTimeOffset now)
    {
        if (Used) return OtpResult.AlreadyUsed;
        if (Attempts >= MaxAttempts) return OtpResult.TooManyAttempts;
        if (now > ExpiresAt) return OtpResult.Expired;

        Attempts++;

        // Fixed-time compare so a wrong code leaks nothing about how wrong it was.
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Code),
            Encoding.UTF8.GetBytes(attempt ?? string.Empty));

        if (!ok) return OtpResult.Wrong;

        Used = true;
        return OtpResult.Ok;
    }

    public OtpSnapshot ToSnapshot() => new(Email, Code, IssuedAt, Attempts, Used);

    public static OtpChallenge FromSnapshot(OtpSnapshot s) => new(s.Email, s.Code, s.IssuedAt) { Attempts = s.Attempts, Used = s.Used };
}
