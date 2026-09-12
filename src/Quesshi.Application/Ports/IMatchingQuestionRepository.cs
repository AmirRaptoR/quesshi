using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Persistence boundary for matching content, deliberately separate from trivia.</summary>
public interface IMatchingQuestionRepository
{
    Task<MatchingQuestion?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MatchingQuestion>> FindAsync(MatchingQuestionFilter filter, CancellationToken ct = default);
    Task<long> CountAsync(MatchingQuestionFilter filter, CancellationToken ct = default);

    /// <summary>Random approved matching questions for a category, excluding ids already served.</summary>
    Task<IReadOnlyList<MatchingQuestion>> SampleApprovedAsync(Language lang, string categoryId, int count,
        IReadOnlyCollection<string> exclude, CancellationToken ct = default);

    Task UpsertAsync(MatchingQuestion question, CancellationToken ct = default);
    /// <summary>Writes a batch, skipping rows rejected by the unique language/topic index.</summary>
    Task<int> UpsertManyAsync(IReadOnlyList<MatchingQuestion> questions, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default);
}
