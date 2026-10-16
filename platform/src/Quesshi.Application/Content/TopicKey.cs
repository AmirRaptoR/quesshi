namespace Quesshi.Application.Content;

internal static class TopicKey
{
    public static string? From(string? subject, string? aspect)
    {
        var left = PromptFingerprint.Normalise(subject); var right = PromptFingerprint.Normalise(aspect);
        return left.Length == 0 || right.Length == 0 ? null : $"{left}|{right}";
    }
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
        var trimmed = value.Trim(); if (trimmed.Length is < 2 or > 80 || trimmed.Any(char.IsDigit)) return false;
        var words = trimmed.Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries); if (words.Length < minWords || words.Length > maxWords) return false;
        var hasAscii = false;
        foreach (var character in trimmed) { if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { hasAscii = true; continue; } if (char.IsLetter(character) || !(char.IsWhiteSpace(character) || character is '-' or '/' or '\'' or '&')) return false; }
        return hasAscii;
    }
}
