namespace Quesshi.Domain;

/// <summary>Shared, deliberately conservative normalization for email sign-in.</summary>
public static class EmailAddress
{
    public const int MaximumLength = 254;

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = Normalize(value);
        if (value.Length > MaximumLength || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = value.LastIndexOf('@');
        if (at is <= 0 || at == value.Length - 1 || value.IndexOf('@') != at)
        {
            return false;
        }

        var domain = value[(at + 1)..];
        return domain.Contains('.') && domain.Split('.').All(part => part.Length > 0);
    }
}
