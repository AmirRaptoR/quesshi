namespace Quesshi.Application.Content;

internal sealed class PromptIndex
{
    private readonly List<Entry> seen = [];
    private readonly List<string> prompts = [];
    public PromptIndex(IEnumerable<(string Prompt, string? Answer)> entries) { foreach (var (prompt, answer) in entries) Add(prompt, answer); }
    public static PromptIndex FromPrompts(IEnumerable<string> prompts) => new(prompts.Select(prompt => (prompt, (string?)null)));
    public int Count => seen.Count;
    public IReadOnlyList<string> Recent(int take) => [.. prompts.TakeLast(take)];
    public bool Contains(string? prompt, string? answer = null)
    {
        var words = PromptFingerprint.ContentWords(prompt);
        return words.Count > 0 && seen.Any(entry => PromptFingerprint.IsDuplicate(words, PromptFingerprint.Normalise(answer), entry.Words, entry.Answer));
    }
    public void Add(string? prompt, string? answer = null)
    {
        var words = PromptFingerprint.ContentWords(prompt);
        if (words.Count == 0) return;
        seen.Add(new(words, PromptFingerprint.Normalise(answer)));
        prompts.Add(prompt!.Trim());
    }
    private sealed record Entry(HashSet<string> Words, string Answer);
}
