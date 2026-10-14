using System.Security.Cryptography;

namespace Quesshi.Domain;

public static class OtpCode
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int Length = 6;
    public const int MaximumAttempts = 5;

    public static string Create() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsValid(string? value) => value is { Length: Length } && value.All(char.IsAsciiDigit);
}
