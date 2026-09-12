using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeMatchingQuestions : IMatchingQuestionRepository
{
    public readonly List<MatchingQuestion> Items = [];

    public Task<MatchingQuestion?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(q => q.Id == id));
    public Task<IReadOnlyList<MatchingQuestion>> FindAsync(MatchingQuestionFilter f, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchingQuestion>>([.. Items.Where(q =>
            (f.Lang is null || q.Lang == f.Lang) && (f.CategoryId is null || q.MatchingCategoryId == f.CategoryId) &&
            (f.Status is null || q.Status == f.Status) &&
            (string.IsNullOrWhiteSpace(f.Text) || q.Prompt.Contains(f.Text, StringComparison.OrdinalIgnoreCase)))
            .Skip(f.Skip).Take(f.Take)]);
    public Task<long> CountAsync(MatchingQuestionFilter f, CancellationToken ct = default)
        => Task.FromResult((long)Items.Count(q =>
            (f.Lang is null || q.Lang == f.Lang) && (f.CategoryId is null || q.MatchingCategoryId == f.CategoryId) &&
            (f.Status is null || q.Status == f.Status) &&
            (string.IsNullOrWhiteSpace(f.Text) || q.Prompt.Contains(f.Text, StringComparison.OrdinalIgnoreCase))));
    public Task<IReadOnlyList<MatchingQuestion>> SampleApprovedAsync(Language lang, string categoryId, int count,
        IReadOnlyCollection<string> exclude, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchingQuestion>>([.. Items.Where(q => q.Status == QuestionStatus.Approved &&
            q.Lang == lang && q.MatchingCategoryId == categoryId && !exclude.Contains(q.Id)).Take(count)]);
    public Task UpsertAsync(MatchingQuestion q, CancellationToken ct = default)
    {
        if (q.Topic is { } topic && Items.Any(x => x.Id != q.Id && x.Lang == q.Lang && x.Topic == topic))
            throw new InvalidOperationException("Duplicate matching topic.");
        Items.RemoveAll(x => x.Id == q.Id);
        Items.Add(q);
        return Task.CompletedTask;
    }
    public async Task<int> UpsertManyAsync(IReadOnlyList<MatchingQuestion> questions, CancellationToken ct = default)
    {
        var stored = 0;
        foreach (var question in questions)
        {
            try { await UpsertAsync(question, ct); stored++; }
            catch (InvalidOperationException) { }
        }
        return stored;
    }
    public Task DeleteAsync(string id, CancellationToken ct = default)
    { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    public Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(Items.Where(q => q.Lang == lang && q.Topic is not null)
            .Select(q => q.Topic!).ToHashSet());
}
