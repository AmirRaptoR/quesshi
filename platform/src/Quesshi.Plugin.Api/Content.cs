using System.Text.Json;

namespace Quesshi.Plugin.Api;

/// <summary>Operations on question envelopes, always within the caller's tenant transaction.</summary>
public interface IContent
{
    Task<QuestionEnvelope?> GetAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuestionEnvelope>> ListAsync(ITenantTx transaction, QuestionFilter filter, CancellationToken cancellationToken = default);
    Task<QuestionWriteResult> CreateAsync(ITenantTx transaction, QuestionWrite request, CancellationToken cancellationToken = default);
    Task<QuestionWriteResult> EditAsync(ITenantTx transaction, Guid id, QuestionWrite request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default);
    Task<bool> SetStatusAsync(ITenantTx transaction, Guid id, QuestionStatus status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuestionBucketCount>> BucketCountsAsync(ITenantTx transaction, CancellationToken cancellationToken = default);
}

public enum QuestionStatus { Draft, Pending, Approved, Rejected }
public enum QuestionSource { Seed, Import, Ai, Admin, Player }
public enum QuestionWriteOutcome { Success, ValidationFailed, Duplicate, Oversize, Missing }

public sealed record QuestionEnvelope(
    Guid Id, string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level,
    string Prompt, JsonElement Payload, string? Explanation, string? Topic, string? Fingerprint,
    QuestionStatus Status, QuestionSource Source, Guid? OwnerId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record QuestionWrite(
    string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level,
    string Prompt, JsonElement Payload, string? Explanation, string? Topic,
    QuestionStatus Status, QuestionSource Source, Guid? OwnerId);

public sealed record QuestionFilter(
    string? Language = null, string? Family = null, string? Kind = null, Guid? CategoryId = null,
    int? Level = null, QuestionStatus? Status = null, QuestionSource? Source = null, string? Prompt = null);

public sealed record QuestionBucketCount(string Language, Guid? CategoryId, int? Level, string Kind, long Approved, long Pending);

public sealed record ContentProblem(string Code, string Message);

public sealed record QuestionWriteResult(QuestionWriteOutcome Outcome, QuestionEnvelope? Question, IReadOnlyList<ContentProblem> Problems)
{
    public static QuestionWriteResult Success(QuestionEnvelope question) => new(QuestionWriteOutcome.Success, question, []);
    public static QuestionWriteResult Validation(params ContentProblem[] problems) => new(QuestionWriteOutcome.ValidationFailed, null, problems);
    public static readonly QuestionWriteResult Duplicate = new(QuestionWriteOutcome.Duplicate, null, []);
    public static readonly QuestionWriteResult Oversize = new(QuestionWriteOutcome.Oversize, null, [new("content.tooLarge", "The resolved card and reveal must not exceed 16 KB.")]);
    public static readonly QuestionWriteResult Missing = new(QuestionWriteOutcome.Missing, null, []);
}
