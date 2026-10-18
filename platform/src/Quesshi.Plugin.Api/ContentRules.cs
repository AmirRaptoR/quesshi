using System.Text;

namespace Quesshi.Plugin.Api;

/// <summary>Compares prompts after removing question wording and spelling variation.</summary>
public static class PromptFingerprint
{
    private const double Threshold = 0.8;
    private const double ThresholdWithSameAnswer = 0.5;
    private const int MinimumSharedWords = 3;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "do", "does", "did",
        "of", "in", "on", "at", "to", "for", "from", "by", "with", "and", "or",
        "what", "which", "who", "whom", "whose", "where", "when", "why", "how",
        "many", "much", "it", "its", "this", "that", "these", "those", "there",
        "has", "have", "had", "you", "your",
        "از", "به", "در", "با", "که", "را", "این", "آن", "برای", "تا", "و", "یا",
        "است", "بود", "هست", "شد", "شده", "می", "کدام", "چه", "چند", "چرا", "کجا", "کی",
        "چگونه", "چطور", "یک", "دارد", "دارند", "داشت", "کرد", "کند", "نام"
    };

    public static bool AreDuplicates(string? a, string? b) => Overlaps(ContentWords(a), ContentWords(b));

    public static bool IsDuplicate(HashSet<string> leftWords, string leftAnswer, HashSet<string> rightWords, string rightAnswer)
    {
        if (Overlaps(leftWords, rightWords)) return true;
        var sameAnswer = leftAnswer.Length > 0 && leftAnswer == rightAnswer;
        if (!sameAnswer) return false;
        var shared = leftWords.Intersect(rightWords).Count();
        return shared >= MinimumSharedWords && Containment(leftWords, rightWords) >= ThresholdWithSameAnswer;
    }

    public static bool Overlaps(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return false;
        return left.Count < 2 || right.Count < 2 ? left.SetEquals(right) : Containment(left, right) >= Threshold;
    }

    public static bool CollidesWith(string? prompt, IEnumerable<string> existing) => existing.Any(e => AreDuplicates(prompt, e));

    public static HashSet<string> ContentWords(string? prompt)
    {
        var words = Normalise(prompt).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var content = words.Where(word => !Filler.Contains(word)).ToHashSet(StringComparer.Ordinal);
        return content.Count > 0 ? content : words;
    }

    public static string Normalise(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;
        var builder = new StringBuilder(prompt.Length);
        foreach (var raw in prompt.Normalize(NormalizationForm.FormKC))
        {
            var character = Canonical(raw);
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (char.IsWhiteSpace(character) || character is '‌') builder.Append(' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double Containment(HashSet<string> left, HashSet<string> right) =>
        left.Count == 0 || right.Count == 0 ? 0 : (double)left.Intersect(right).Count() / Math.Min(left.Count, right.Count);

    private static char Canonical(char character) => character switch
    {
        'ي' or 'ى' => 'ی',
        'ك' => 'ک',
        'ة' => 'ه',
        'أ' or 'إ' or 'آ' => 'ا',
        >= '٠' and <= '٩' => (char)('0' + (character - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (character - '۰')),
        _ => character,
    };
}

public sealed class PromptIndex
{
    private readonly List<Entry> _seen = [];
    private readonly List<string> _prompts = [];

    public PromptIndex(IEnumerable<(string Prompt, string? Answer)> entries)
    {
        foreach (var (prompt, answer) in entries) Add(prompt, answer);
    }

    public static PromptIndex FromPrompts(IEnumerable<string> prompts) => new(prompts.Select(prompt => (prompt, (string?)null)));
    public int Count => _seen.Count;
    public IReadOnlyList<string> Recent(int take) => [.. _prompts.TakeLast(take)];

    public bool Contains(string? prompt, string? answer = null)
    {
        var words = PromptFingerprint.ContentWords(prompt);
        if (words.Count == 0) return false;
        var key = PromptFingerprint.Normalise(answer);
        return _seen.Any(entry => PromptFingerprint.IsDuplicate(words, key, entry.Words, entry.Answer));
    }

    public void Add(string? prompt, string? answer = null)
    {
        var words = PromptFingerprint.ContentWords(prompt);
        if (words.Count == 0) return;
        _seen.Add(new(words, PromptFingerprint.Normalise(answer)));
        _prompts.Add(prompt!.Trim());
    }

    private sealed record Entry(HashSet<string> Words, string Answer);
}

public static class TopicKey
{
    public static string? From(string? subject, string? aspect)
    {
        var left = PromptFingerprint.Normalise(subject);
        var right = PromptFingerprint.Normalise(aspect);
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
        var trimmed = value.Trim();
        if (trimmed.Length is < 2 or > 80 || trimmed.Any(char.IsDigit)) return false;
        var words = trimmed.Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < minWords || words.Length > maxWords) return false;
        var hasAsciiLetter = false;
        foreach (var character in trimmed)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { hasAsciiLetter = true; continue; }
            if (char.IsLetter(character)) return false;
            if (!(char.IsWhiteSpace(character) || character is '-' or '/' or '\'' or '&')) return false;
        }
        return hasAsciiLetter;
    }
}
