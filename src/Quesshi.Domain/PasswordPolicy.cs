namespace Quesshi.Domain;

/// <summary>
/// Length first, composition rules never. A long passphrase beats "P@ssw0rd", and forcing symbols
/// mostly produces predictable substitutions. The blocklist catches the handful of guesses an
/// attacker tries before anything else.
///
/// Length also buys freedom: a short password may not contain a blocked word at all, while a long
/// passphrase is only refused if it *is* one. Otherwise "a whole new password" would be rejected,
/// which teaches people that the rules are arbitrary.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    /// <summary>An upper bound so a giant password cannot turn the hasher into a denial of service.</summary>
    public const int MaxLength = 200;

    /// <summary>At or above this length, only an exact match with a blocked word is refused.</summary>
    public const int PassphraseLength = 16;

    private static readonly string[] Obvious =
    [
        "password", "passw0rd", "12345678", "123456789", "qwerty", "letmein",
        "admin", "administrator", "quesshi", "welcome", "changeme", "iloveyou"
    ];

    public static IReadOnlyList<string> Problems(string? password)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(password))
        {
            problems.Add("password.blank");
            return problems;
        }

        if (password.Length < MinLength) problems.Add("password.tooShort");
        if (password.Length > MaxLength) problems.Add("password.tooLong");

        var folded = password.Trim().ToLowerInvariant();
        var letters = new string([.. folded.Where(char.IsLetter)]);

        var tooObvious = folded.Length >= PassphraseLength
            ? Obvious.Any(o => letters == o)
            : Obvious.Any(o => folded.Contains(o, StringComparison.Ordinal));

        if (tooObvious) problems.Add("password.tooObvious");

        return problems;
    }

    public static bool IsAcceptable(string? password) => Problems(password).Count == 0;
}
