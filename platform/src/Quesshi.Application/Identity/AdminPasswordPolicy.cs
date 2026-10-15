namespace Quesshi.Application.Identity;

internal static class AdminPasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 200;
    private const int PassphraseLength = 16;

    private static readonly string[] Obvious =
    [
        "password", "passw0rd", "12345678", "123456789", "qwerty", "letmein",
        "admin", "administrator", "quesshi", "welcome", "changeme", "iloveyou"
    ];

    public static IReadOnlyList<string> Problems(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return ["password.blank"];

        var problems = new List<string>();
        if (password.Length < MinLength) problems.Add("password.tooShort");
        if (password.Length > MaxLength) problems.Add("password.tooLong");

        var folded = password.Trim().ToLowerInvariant();
        var letters = new string([.. folded.Where(char.IsLetter)]);
        var tooObvious = folded.Length >= PassphraseLength
            ? Obvious.Any(word => letters == word)
            : Obvious.Any(word => folded.Contains(word, StringComparison.Ordinal));
        if (tooObvious) problems.Add("password.tooObvious");
        return problems;
    }
}
