namespace Quesshi.Domain;

/// <summary>
/// A deliberately loose check: enough to catch a typo or an empty box, not an attempt to implement
/// RFC 5322. The only real proof an address works is that mail sent to it arrives.
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;

    public static string Normalise(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool LooksValid(string? value)
    {
        var email = Normalise(value);

        if (email.Length is 0 or > MaxLength) return false;
        if (email.Any(char.IsWhiteSpace)) return false;

        var parts = email.Split('@');
        if (parts.Length != 2) return false;

        var (local, domain) = (parts[0], parts[1]);
        if (local.Length == 0 || domain.Length == 0) return false;

        // A domain that can receive mail has a dot, and no empty label either side of it.
        if (!domain.Contains('.')) return false;

        return domain.Split('.').All(label => label.Length > 0);
    }
}
