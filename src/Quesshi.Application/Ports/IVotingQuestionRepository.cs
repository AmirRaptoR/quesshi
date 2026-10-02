using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Persistence boundary for voting content, deliberately separate from trivia.</summary>
public interface IVotingQuestionRepository
{
    Task<VotingQuestion?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<VotingQuestion>> FindAsync(VotingQuestionFilter filter, CancellationToken ct = default);
    Task<long> CountAsync(VotingQuestionFilter filter, CancellationToken ct = default);

    /// <summary>Random approved voting questions for a category, excluding ids already served.</summary>
    Task<IReadOnlyList<VotingQuestion>> SampleApprovedAsync(Language lang, string categoryId, int count,
        IReadOnlyCollection<string> exclude, CancellationToken ct = default);

    Task UpsertAsync(VotingQuestion question, CancellationToken ct = default);
    /// <summary>Atomically records one newly served match slot. A token is unique per match/slot,
    /// so replay after a crash is harmless and two matches cannot lose an increment.</summary>
    Task<VotingServeResult> RecordServedAsync(string id, string serveToken, CancellationToken ct = default);
    /// <summary>Writes a batch, skipping rows rejected by the unique language/topic index.</summary>
    Task<int> UpsertManyAsync(IReadOnlyList<VotingQuestion> questions, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default);
    /// <summary>Every prompt in one language/category, used to keep generated paraphrases out.</summary>
    Task<IReadOnlyCollection<string>> ExistingPromptsAsync(Language lang, string categoryId,
        CancellationToken ct = default);
}

public enum VotingServeResult
{
    Missing,
    Recorded,
    AlreadyRecorded
}
