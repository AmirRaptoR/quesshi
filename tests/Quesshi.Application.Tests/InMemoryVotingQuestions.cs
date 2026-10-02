using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryVotingQuestions : IVotingQuestionRepository
{
    public readonly List<VotingQuestion> Items = [];
    private readonly HashSet<string> _servedTokens = [];
    private readonly Random _rng = new(1234);

    public Task<VotingQuestion?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(q => q.Id == id));

    public Task<IReadOnlyList<VotingQuestion>> FindAsync(VotingQuestionFilter f, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VotingQuestion>>([.. Match(f).Skip(f.Skip).Take(f.Take)]);

    public Task<long> CountAsync(VotingQuestionFilter f, CancellationToken ct = default)
        => Task.FromResult((long)Match(f).Count());

    private IEnumerable<VotingQuestion> Match(VotingQuestionFilter f) => Items.Where(q =>
        (f.Lang is null || q.Lang == f.Lang) &&
        (f.CategoryId is null || q.VotingCategoryId == f.CategoryId) &&
        (f.Status is null || q.Status == f.Status) &&
        (string.IsNullOrWhiteSpace(f.Text) || q.Prompt.Contains(f.Text, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<VotingQuestion>> SampleApprovedAsync(Language lang, string categoryId, int count,
        IReadOnlyCollection<string> exclude, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VotingQuestion>>([.. Items
            .Where(q => q.Status == QuestionStatus.Approved && q.Lang == lang &&
                        q.VotingCategoryId == categoryId && !exclude.Contains(q.Id))
            .OrderBy(_ => _rng.Next()).Take(count)]);

    public Task UpsertAsync(VotingQuestion q, CancellationToken ct = default)
    {
        if (q.Topic is { } topic && Items.Any(x => x.Id != q.Id && x.Lang == q.Lang && x.Topic == topic))
            throw new InvalidOperationException("Duplicate voting topic.");
        Items.RemoveAll(x => x.Id == q.Id);
        Items.Add(q);
        return Task.CompletedTask;
    }

    public Task<VotingServeResult> RecordServedAsync(string id, string serveToken,
        CancellationToken ct = default)
    {
        var question = Items.FirstOrDefault(q => q.Id == id);
        if (question is null) return Task.FromResult(VotingServeResult.Missing);
        if (!_servedTokens.Add(serveToken)) return Task.FromResult(VotingServeResult.AlreadyRecorded);
        question.RecordServed();
        return Task.FromResult(VotingServeResult.Recorded);
    }

    public async Task<int> UpsertManyAsync(IReadOnlyList<VotingQuestion> questions, CancellationToken ct = default)
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
    {
        Items.RemoveAll(x => x.Id == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(Items.Where(q => q.Lang == lang && q.Topic is not null)
            .Select(q => q.Topic!).ToHashSet());

    public Task<IReadOnlyCollection<string>> ExistingPromptsAsync(Language lang, string categoryId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<string>>([.. Items
            .Where(q => q.Lang == lang && q.VotingCategoryId == categoryId)
            .Select(q => q.Prompt)]);
}
