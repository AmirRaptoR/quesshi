using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeQuestions : IQuestionRepository
{
    public readonly List<Question> Items = [];

    public Task<Question?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(q => q.Id == id));
    public Task<IReadOnlyList<Question>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. ids.Select(i => Items.First(q => q.Id == i))]);
    public Task<IReadOnlyList<Question>> FindAsync(QuestionFilter f, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Question>>([.. Items]);
    public Task<long> CountAsync(QuestionFilter f, CancellationToken ct = default) => Task.FromResult((long)Items.Count);
    public Task<IReadOnlyList<Question>> SampleApprovedAsync(Language lang, string c, Difficulty l, int n, IReadOnlyCollection<string> ex, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Question>>([.. Items.Where(q => q.CategoryId == c && q.Level == l && q.Lang == lang && !ex.Contains(q.Id)).Take(n)]);
    public Task<IReadOnlyList<BucketCount>> BucketCountsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BucketCount>>([]);
    public Task UpsertAsync(Question q, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == q.Id); Items.Add(q); return Task.CompletedTask; }
    public Task<int> UpsertManyAsync(IReadOnlyList<Question> qs, CancellationToken ct = default) { foreach (var q in qs) UpsertAsync(q, ct); return Task.FromResult(qs.Count); }
    public Task DeleteAsync(string id, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    public Task<IReadOnlyCollection<(string Prompt, string Answer)>> ExistingQuestionsAsync(string categoryId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<(string, string)>>([]);
}
