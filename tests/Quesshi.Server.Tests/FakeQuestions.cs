using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeQuestions : IQuestionRepository
{
    public readonly List<Question> Items = [];

    public Task<Question?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(q => q.Id == id));
    // Mirrors MongoQuestionRepository.GetManyAsync: a missing id is skipped, not an error.
    public Task<IReadOnlyList<Question>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. ids.Select(i => Items.FirstOrDefault(q => q.Id == i)).Where(q => q is not null)!]);
    public Task<IReadOnlyList<Question>> FindAsync(QuestionFilter f, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Question>>([.. Items]);
    public Task<long> CountAsync(QuestionFilter f, CancellationToken ct = default) => Task.FromResult((long)Items.Count);
    public Task<IReadOnlyList<Question>> SampleApprovedAsync(Language lang, string c, Difficulty l, int n, IReadOnlyCollection<string> ex, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. Items.Where(q => q.CategoryId == c && q.Level == l && q.Lang == lang && !ex.Contains(q.Id)).Take(n)]);
    /// <summary>
    /// Grouped by kind as well as by (language, category, level), exactly as the real repository
    /// does. It used to answer with nothing at all, which was harmless while a bucket was one thing;
    /// it stopped being harmless once the dashboard's job became showing that a category full of
    /// choice questions has no sorting questions in it.
    /// </summary>
    public Task<IReadOnlyList<BucketCount>> BucketCountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BucketCount>>([.. Items
            .GroupBy(q => (q.Lang, q.CategoryId, q.Level, q.Kind))
            .Select(g => new BucketCount(g.Key.Lang, g.Key.CategoryId, g.Key.Level,
                g.Count(q => q.Status == QuestionStatus.Approved),
                g.Count(q => q.Status == QuestionStatus.Pending),
                g.Key.Kind))]);
    public Task UpsertAsync(Question q, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == q.Id); Items.Add(q); return Task.CompletedTask; }
    public Task<int> UpsertManyAsync(IReadOnlyList<Question> qs, CancellationToken ct = default) { foreach (var q in qs) UpsertAsync(q, ct); return Task.FromResult(qs.Count); }
    public Task DeleteAsync(string id, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    public Task<IReadOnlyCollection<(string Prompt, string Answer)>> ExistingQuestionsAsync(string categoryId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<(string, string)>>([]);

    public Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(Items.Where(q => q.Lang == lang && q.Topic is { Length: > 0 })
            .Select(q => q.Topic!).ToHashSet());
}
