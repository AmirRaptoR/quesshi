namespace Quesshi.Domain;

/// <summary>
/// The questions a category already contains, held as content-word sets so a batch of candidates
/// can be checked without re-parsing the bank for every one.
/// </summary>
public sealed class PromptIndex
{
    private readonly List<Entry> _seen = [];
    private readonly List<string> _prompts = [];

    public PromptIndex(IEnumerable<(string Prompt, string? Answer)> entries)
    {
        foreach (var (prompt, answer) in entries) Add(prompt, answer);
    }

    /// <summary>For callers that only have the wording. Named, because two constructors taking a sequence are ambiguous.</summary>
    public static PromptIndex FromPrompts(IEnumerable<string> prompts)
        => new(prompts.Select(p => (p, (string?)null)));

    public int Count => _seen.Count;

    /// <summary>The most recent prompts verbatim, to show the model what not to write again.</summary>
    public IReadOnlyList<string> Recent(int take) => [.. _prompts.TakeLast(take)];

    public bool Contains(string? prompt, string? answer = null)
    {
        var words = PromptFingerprint.ContentWords(prompt);
        if (words.Count == 0) return false;

        var key = PromptFingerprint.Normalise(answer);
        return _seen.Any(e => PromptFingerprint.IsDuplicate(words, key, e.Words, e.Answer));
    }

    public void Add(string? prompt, string? answer = null)
    {
        var words = PromptFingerprint.ContentWords(prompt);
        if (words.Count == 0) return;

        _seen.Add(new Entry(words, PromptFingerprint.Normalise(answer)));
        _prompts.Add(prompt!.Trim());
    }

    private sealed record Entry(HashSet<string> Words, string Answer);
}
