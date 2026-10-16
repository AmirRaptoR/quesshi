using System.Text;

namespace Quesshi.Application.Content;

/// <summary>Today's language-aware prompt duplicate rule, copied for the platform content bank.</summary>
internal static class PromptFingerprint
{
    private const double Threshold = 0.8;
    private const double ThresholdWithSameAnswer = 0.5;
    private const int MinimumSharedWords = 3;
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "do", "does", "did", "of", "in", "on", "at", "to", "for", "from", "by", "with", "and", "or", "what", "which", "who", "whom", "whose", "where", "when", "why", "how", "many", "much", "it", "its", "this", "that", "these", "those", "there", "has", "have", "had", "you", "your",
        "از", "به", "در", "با", "که", "را", "این", "آن", "برای", "تا", "و", "یا", "است", "بود", "هست", "شد", "شده", "می", "کدام", "چه", "چند", "چرا", "کجا", "کی", "چگونه", "چطور", "یک", "دارد", "دارند", "داشت", "کرد", "کند", "نام"
    };

    public static bool AreDuplicates(string? left, string? right) => Overlaps(ContentWords(left), ContentWords(right));
    public static bool IsDuplicate(HashSet<string> left, string leftAnswer, HashSet<string> right, string rightAnswer)
    {
        if (Overlaps(left, right)) return true;
        return leftAnswer.Length > 0 && leftAnswer == rightAnswer && left.Intersect(right).Count() >= MinimumSharedWords && Containment(left, right) >= ThresholdWithSameAnswer;
    }
    public static bool Overlaps(HashSet<string> left, HashSet<string> right)
        => left.Count > 0 && right.Count > 0 && (left.Count < 2 || right.Count < 2 ? left.SetEquals(right) : Containment(left, right) >= Threshold);
    public static bool CollidesWith(string? prompt, IEnumerable<string> existing) => existing.Any(candidate => AreDuplicates(prompt, candidate));
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
    private static double Containment(HashSet<string> left, HashSet<string> right)
        => left.Count == 0 || right.Count == 0 ? 0 : (double)left.Intersect(right).Count() / Math.Min(left.Count, right.Count);
    private static char Canonical(char character) => character switch
    {
        'ي' or 'ى' => 'ی', 'ك' => 'ک', 'ة' => 'ه', 'أ' or 'إ' or 'آ' => 'ا',
        >= '٠' and <= '٩' => (char)('0' + (character - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (character - '۰')), _ => character
    };
}
