using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryQuestions : IQuestionRepository
{
    public readonly List<Question> Items = [];
    private readonly Random _rng = new(1234);

    public Task<Question?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(q => q.Id == id));

    public Task<IReadOnlyList<Question>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. ids.Select(i => Items.FirstOrDefault(q => q.Id == i)).OfType<Question>()]);

    public Task<IReadOnlyList<Question>> FindAsync(QuestionFilter f, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. Match(f).Skip(f.Skip).Take(f.Take)]);

    public Task<long> CountAsync(QuestionFilter f, CancellationToken ct = default)
        => Task.FromResult((long)Match(f).Count());

    private IEnumerable<Question> Match(QuestionFilter f) => Items.Where(q =>
        (f.Lang is null || q.Lang == f.Lang) &&
        (f.CategoryId is null || q.CategoryId == f.CategoryId) &&
        (f.Level is null || q.Level == f.Level) &&
        (f.Status is null || q.Status == f.Status) &&
        (f.Text is null || q.Prompt.Contains(f.Text, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Question>> SampleApprovedAsync(Language lang, string categoryId, Difficulty level, int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. Items
            .Where(q => q.Status == QuestionStatus.Approved && q.Lang == lang && q.CategoryId == categoryId && q.Level == level && !exclude.Contains(q.Id))
            .OrderBy(_ => _rng.Next())
            .Take(count)]);

    /// <summary>
    /// Grouped by kind as well, exactly as <c>MongoQuestionRepository</c> does. A fake that grouped
    /// only by (language, category, level) would report a bank of 3067 choice questions as full
    /// buckets and the top-up under test would then correctly decide there was nothing to do — the
    /// real bug, passing as a green test, because the fake was the thing that had been fixed.
    /// </summary>
    public Task<IReadOnlyList<BucketCount>> BucketCountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BucketCount>>([.. Items
            .GroupBy(q => (q.Lang, q.CategoryId, q.Level, q.Kind))
            .Select(g => new BucketCount(g.Key.Lang, g.Key.CategoryId, g.Key.Level,
                g.Count(q => q.Status == QuestionStatus.Approved),
                g.Count(q => q.Status == QuestionStatus.Pending),
                g.Key.Kind))]);

    public Task UpsertAsync(Question q, CancellationToken ct = default)
    {
        Items.RemoveAll(x => x.Id == q.Id);
        Items.Add(q);
        return Task.CompletedTask;
    }

    /// <summary>Stands in for the store's unique index on (language, topic), and counts what landed.</summary>
    public async Task<int> UpsertManyAsync(IReadOnlyList<Question> qs, CancellationToken ct = default)
    {
        var stored = 0;

        foreach (var q in qs)
        {
            if (q.Topic is { Length: > 0 } topic
                && Items.Any(existing => existing.Id != q.Id && existing.Lang == q.Lang && existing.Topic == topic))
                continue;

            await UpsertAsync(q, ct);
            stored++;
        }

        return stored;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        Items.RemoveAll(x => x.Id == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<(string Prompt, string Answer)>> ExistingQuestionsAsync(string categoryId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<(string, string)>>(
            // Bounds-checked exactly as MongoQuestionRepository is, because a map question stores no
            // choices at all: indexing blind would make this fake throw where the real repository
            // quietly returns "", which is the worst kind of difference between the two.
            [.. Items.Where(q => q.CategoryId == categoryId).Select(q => (q.Prompt,
                q.CorrectIndex >= 0 && q.CorrectIndex < q.Choices.Count ? q.Choices[q.CorrectIndex] : ""))]);

    public Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(Items.Where(q => q.Lang == lang && q.Topic is { Length: > 0 })
            .Select(q => q.Topic!).ToHashSet());
}
