using System.Globalization;
using System.Text;

namespace Quesshi.Domain;

/// <summary>
/// Decides whether two question prompts are really the same question.
///
/// An exact string comparison is not enough: a language model asked twice for "geography, easy"
/// will happily return "What is the capital of France?" and "Which city is the capital of France?".
/// Those are one question. The trick is to throw away the question's *form* — casing, punctuation,
/// Persian spelling variants, and the filler words that carry no subject — and compare what is left.
/// </summary>
public static class PromptFingerprint
{
    /// <summary>How much of the shorter prompt's content must appear in the longer one.</summary>
    private const double Threshold = 0.8;

    /// <summary>
    /// A softer bar, used only when both questions have the same answer. Padding a question with
    /// scene-setting words ("one of the driest places on Earth") drops the overlap well below the
    /// strict threshold while leaving it the same question; the shared answer is what tells them
    /// apart from two unrelated questions that merely happen to overlap.
    /// </summary>
    private const double ThresholdWithSameAnswer = 0.5;

    /// <summary>
    /// How many content words two questions must actually share before a common answer counts as
    /// evidence. Without this, a pair of two-word prompts that happen to share one word and an
    /// answer would look like a duplicate on almost no evidence at all.
    /// </summary>
    private const int MinimumSharedWords = 3;

    // ponytail: token containment, not meaning. It catches the duplicate a model actually produces
    // — the same subject with the question reworded — but not a genuine paraphrase such as
    // "Who wrote Ulysses?" against "Ulysses was written by whom?". Swap in sentence embeddings
    // if that starts mattering; nothing else here would have to change.

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        // English question scaffolding
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "do", "does", "did",
        "of", "in", "on", "at", "to", "for", "from", "by", "with", "and", "or",
        "what", "which", "who", "whom", "whose", "where", "when", "why", "how",
        "many", "much", "it", "its", "this", "that", "these", "those", "there",
        "has", "have", "had", "you", "your",

        // Persian question scaffolding
        "از", "به", "در", "با", "که", "را", "این", "آن", "برای", "تا", "و", "یا",
        "است", "بود", "هست", "شد", "شده", "می", "کدام", "چه", "چند", "چرا", "کجا", "کی",
        "چگونه", "چطور", "یک", "دارد", "دارند", "داشت", "کرد", "کند", "نام"
    };

    public static bool AreDuplicates(string? a, string? b)
        => Overlaps(ContentWords(a), ContentWords(b));

    /// <summary>Duplicate on wording alone, or on wording plus a shared answer.</summary>
    public static bool IsDuplicate(HashSet<string> leftWords, string leftAnswer, HashSet<string> rightWords, string rightAnswer)
    {
        if (Overlaps(leftWords, rightWords)) return true;

        var sameAnswer = leftAnswer.Length > 0 && leftAnswer == rightAnswer;
        if (!sameAnswer) return false;

        var shared = leftWords.Intersect(rightWords).Count();
        return shared >= MinimumSharedWords && Containment(leftWords, rightWords) >= ThresholdWithSameAnswer;
    }

    /// <summary>The comparison itself, on prepared word sets.</summary>
    public static bool Overlaps(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return false;

        // One content word is too little to judge on: "France" appears in plenty of unrelated
        // questions. Below two words, only an exact match counts.
        if (left.Count < 2 || right.Count < 2) return left.SetEquals(right);

        return Containment(left, right) >= Threshold;
    }

    /// <summary>How much of the shorter set the two share.</summary>
    private static double Containment(HashSet<string> left, HashSet<string> right)
        => left.Count == 0 || right.Count == 0 ? 0 : (double)left.Intersect(right).Count() / Math.Min(left.Count, right.Count);

    public static bool CollidesWith(string? prompt, IEnumerable<string> existing)
        => existing.Any(e => AreDuplicates(prompt, e));

    /// <summary>The words that carry the subject, once form and spelling variation are stripped out.</summary>
    public static HashSet<string> ContentWords(string? prompt)
    {
        var words = Normalise(prompt)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        var content = words.Where(w => !Filler.Contains(w)).ToHashSet(StringComparer.Ordinal);

        // A prompt made entirely of filler still has to compare against something.
        return content.Count > 0 ? content : words;
    }

    /// <summary>Lower case, no punctuation, no diacritics, and one canonical spelling per letter.</summary>
    public static string Normalise(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;

        var builder = new StringBuilder(prompt.Length);

        foreach (var raw in prompt.Normalize(NormalizationForm.FormKC))
        {
            var ch = Canonical(raw);

            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
            else if (char.IsWhiteSpace(ch) || ch is '‌') builder.Append(' ');
            // everything else — punctuation, marks, symbols — is dropped
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Arabic and Persian keyboards produce different codepoints for the same letter.</summary>
    private static char Canonical(char ch) => ch switch
    {
        'ي' or 'ى' => 'ی',   // Arabic yeh / alef maksura -> Persian yeh
        'ك' => 'ک',               // Arabic kaf -> Persian keheh
        'ة' => 'ه',               // teh marbuta -> heh
        'أ' or 'إ' or 'آ' => 'ا', // hamza forms -> alef
        >= '٠' and <= '٩' => (char)('0' + (ch - '٠')), // Arabic-Indic digits
        >= '۰' and <= '۹' => (char)('0' + (ch - '۰')), // Persian digits
        _ => ch
    };
}
