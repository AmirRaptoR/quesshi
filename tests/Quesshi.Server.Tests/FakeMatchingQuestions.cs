using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeMatchingQuestions : IMatchingQuestionRepository
{
    public readonly List<MatchingQuestion> Items = [];
    public readonly Dictionary<string, int> UpsertCounts = [];
    public readonly Dictionary<string, int> RecordServedQuestionCounts = [];
    private readonly HashSet<string> _servedTokens = [];
    private readonly object _serveGate = new();
    public int RecordServedCalls { get; private set; }
    public int FailRecordServedCalls { get; set; }

    public Task<MatchingQuestion?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(q => q.Id == id) is { } question ? Clone(question) : null);
    public Task<IReadOnlyList<MatchingQuestion>> FindAsync(MatchingQuestionFilter f, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchingQuestion>>([.. Items.Where(q =>
            (f.Lang is null || q.Lang == f.Lang) && (f.CategoryId is null || q.MatchingCategoryId == f.CategoryId) &&
            (f.Status is null || q.Status == f.Status) &&
            (string.IsNullOrWhiteSpace(f.Text) || q.Prompt.Contains(f.Text, StringComparison.OrdinalIgnoreCase)))
            .Select(q => Clone(q))
            .Skip(f.Skip).Take(f.Take)]);
    public Task<long> CountAsync(MatchingQuestionFilter f, CancellationToken ct = default)
        => Task.FromResult((long)Items.Count(q =>
            (f.Lang is null || q.Lang == f.Lang) && (f.CategoryId is null || q.MatchingCategoryId == f.CategoryId) &&
            (f.Status is null || q.Status == f.Status) &&
            (string.IsNullOrWhiteSpace(f.Text) || q.Prompt.Contains(f.Text, StringComparison.OrdinalIgnoreCase))));
    public Task<IReadOnlyList<MatchingQuestion>> SampleApprovedAsync(Language lang, string categoryId, int count,
        IReadOnlyCollection<string> exclude, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchingQuestion>>([.. Items.Where(q => q.Status == QuestionStatus.Approved &&
            q.Lang == lang && q.MatchingCategoryId == categoryId && !exclude.Contains(q.Id)).Take(count).Select(q => Clone(q))]);
    public Task UpsertAsync(MatchingQuestion q, CancellationToken ct = default)
    {
        if (q.Topic is { } topic && Items.Any(x => x.Id != q.Id && x.Lang == q.Lang && x.Topic == topic))
            throw new InvalidOperationException("Duplicate matching topic.");
        var previous = Items.FirstOrDefault(x => x.Id == q.Id);
        Items.RemoveAll(x => x.Id == q.Id);
        Items.Add(Clone(q, previous?.TimesServed ?? q.TimesServed));
        UpsertCounts[q.Id] = UpsertCounts.GetValueOrDefault(q.Id) + 1;
        return Task.CompletedTask;
    }

    public Task<MatchingServeResult> RecordServedAsync(string id, string serveToken,
        CancellationToken ct = default)
    {
        lock (_serveGate)
        {
            RecordServedCalls++;
            if (FailRecordServedCalls > 0)
            {
                FailRecordServedCalls--;
                throw new InvalidOperationException("matching counter unavailable");
            }

            var question = Items.FirstOrDefault(q => q.Id == id);
            if (question is null) return Task.FromResult(MatchingServeResult.Missing);
            if (!_servedTokens.Add(serveToken)) return Task.FromResult(MatchingServeResult.AlreadyRecorded);
            RecordServedQuestionCounts[id] = RecordServedQuestionCounts.GetValueOrDefault(id) + 1;
            question.RecordServed();
            return Task.FromResult(MatchingServeResult.Recorded);
        }
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

    public Task<IReadOnlyCollection<string>> ExistingPromptsAsync(Language lang, string categoryId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyCollection<string>>([.. Items
            .Where(q => q.Lang == lang && q.MatchingCategoryId == categoryId)
            .Select(q => q.Prompt)]);

    private static MatchingQuestion Clone(MatchingQuestion q, int? timesServed = null)
        => MatchingQuestion.Restore(q.Id, q.Lang, q.MatchingCategoryId, q.Prompt, q.AnswerSource,
            q.FixedChoices, q.Media, q.Status, q.Source, q.Topic, q.CreatedAt, q.UpdatedAt,
            timesServed ?? q.TimesServed);
}
