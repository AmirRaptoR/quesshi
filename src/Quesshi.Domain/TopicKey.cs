namespace Quesshi.Domain;

/// <summary>
/// What a question is *about*, independent of how it is worded: a subject and the aspect of it
/// being asked. "Wie regisseerde Inception?" and "Inception werd geregisseerd door wie?" share no
/// content words at all, but both are inception|director.
///
/// The generator supplies both halves. Uniqueness is then enforced per language in the database,
/// which is the only place that can enforce it against writers that do not know about each other.
/// </summary>
public static class TopicKey
{
    /// <summary>
    /// Null when either half is missing — a question with no key is simply not covered by the
    /// unique index, which is what keeps the hand-written seed bank working unchanged.
    /// </summary>
    public static string? From(string? subject, string? aspect)
    {
        var left = PromptFingerprint.Normalise(subject);
        var right = PromptFingerprint.Normalise(aspect);

        return left.Length == 0 || right.Length == 0 ? null : $"{left}|{right}";
    }

    /// <summary>
    /// Builds an identity supplied by the question generator. Generated identities are deliberately
    /// stricter than hand-authored ones: the prompt asks for short English concepts so keys remain
    /// stable across the language of the visible question. Counters and translated sentences are
    /// model workarounds, not identities, and accepting them defeats the unique index.
    /// </summary>
    public static string? FromGenerated(string? subject, string? aspect)
    {
        if (!IsEnglishConcept(subject, 2, 6) || !IsEnglishConcept(aspect, 1, 4)) return null;

        var key = From(subject, aspect);
        if (key is null) return null;

        var parts = key.Split('|', 2);
        return PromptFingerprint.AreDuplicates(parts[0], parts[1]) ? null : key;
    }

    private static bool IsEnglishConcept(string? value, int minWords, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (trimmed.Length is < 2 or > 80 || trimmed.Any(char.IsDigit)) return false;

        var words = trimmed.Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < minWords || words.Length > maxWords) return false;

        var hasAsciiLetter = false;
        foreach (var character in trimmed)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                hasAsciiLetter = true;
                continue;
            }

            if (char.IsLetter(character)) return false;
            if (!(char.IsWhiteSpace(character) || character is '-' or '/' or '\'' or '&')) return false;
        }

        return hasAsciiLetter;
    }
}
