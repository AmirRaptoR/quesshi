using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IQuestionRepository
{
    Task<Question?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Question>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
    Task<IReadOnlyList<Question>> FindAsync(QuestionFilter filter, CancellationToken ct = default);
    Task<long> CountAsync(QuestionFilter filter, CancellationToken ct = default);

    /// <summary>Random approved questions for a bucket, excluding ids already used in this match.</summary>
    Task<IReadOnlyList<Question>> SampleApprovedAsync(Language lang, string categoryId, Difficulty level, int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default);

    Task<IReadOnlyList<BucketCount>> BucketCountsAsync(CancellationToken ct = default);
    Task UpsertAsync(Question question, CancellationToken ct = default);
    /// <summary>
    /// Writes a batch and returns how many actually landed. A question whose topic already exists
    /// in that language is rejected by the store's unique index and silently skipped, so the count
    /// can be lower than what was handed in.
    /// </summary>
    Task<int> UpsertManyAsync(IReadOnlyList<Question> questions, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Existing questions in a category as (prompt, correct answer), used to keep the generator
    /// from inserting duplicates. The answer matters: two questions with the same answer and
    /// similar wording are the same question however much padding one of them carries.
    /// </summary>
    Task<IReadOnlyCollection<(string Prompt, string Answer)>> ExistingQuestionsAsync(string categoryId, CancellationToken ct = default);

    /// <summary>
    /// Every distinct, non-null topic already stored for a language — what a bulk import checks a
    /// row's <see cref="TopicKey"/> against before anything is written. The store's own unique index
    /// on (language, topic) enforces this for a real write, but a dry run has nothing written yet to
    /// check that index against, so the check has to be explicit here too.
    /// </summary>
    Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default);
}
