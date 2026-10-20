using System.Text.Json;

namespace Quesshi.Plugin.Api;

/// <summary>Accesses question-bank content within the caller's tenant transaction.</summary>
public interface IContent
{
    Task<QuestionEnvelope?> GetAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuestionEnvelope>> ListAsync(ITenantTx transaction, QuestionFilter? filter = null, CancellationToken cancellationToken = default);
    Task<QuestionWriteResult> CreateAsync(ITenantTx transaction, QuestionWrite request, CancellationToken cancellationToken = default);
    Task<QuestionWriteResult> EditAsync(ITenantTx transaction, Guid id, QuestionWrite request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default);
    Task<bool> SetStatusAsync(ITenantTx transaction, Guid id, QuestionStatus status, CancellationToken cancellationToken = default);
    Task<bool> SetStatusAsync(ITenantTx transaction, IReadOnlyList<Guid> ids, QuestionStatus status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuestionBucketCount>> BucketCountsAsync(ITenantTx transaction, QuestionFilter? filter = null, CancellationToken cancellationToken = default);
}

public sealed record QuestionEnvelope(
    Guid Id,
    string Kind,
    int KindVersion,
    string Family,
    string Language,
    Guid? CategoryId,
    int? Level,
    string Prompt,
    JsonElement Payload,
    string? Explanation,
    string? Topic,
    string Fingerprint,
    QuestionStatus Status,
    QuestionSource Source,
    Guid? OwnerId,
    Guid? MediaId = null);

public sealed record QuestionWrite(
    string? Kind,
    int KindVersion,
    string? Family,
    string? Language,
    Guid? CategoryId,
    int? Level,
    string? Prompt,
    JsonElement Payload,
    string? Explanation,
    string? Topic,
    QuestionStatus Status,
    QuestionSource Source,
    Guid? OwnerId,
    Guid? MediaId = null);

public sealed record QuestionFilter(
    string? Language = null,
    string? Family = null,
    string? Kind = null,
    Guid? CategoryId = null,
    int? Level = null,
    QuestionStatus? Status = null,
    QuestionSource? Source = null,
    string? Prompt = null);

public sealed record QuestionBucketCount(string Language, Guid? CategoryId, int? Level, string Kind, int Approved, int Pending);

public sealed record ContentProblem(string Code, string Message);

public enum QuestionWriteStatus
{
    Success,
    Missing,
    Invalid,
    Duplicate,
    Oversize,
}

public sealed record QuestionWriteResult(QuestionWriteStatus Status, QuestionEnvelope? Question = null, IReadOnlyList<ContentProblem>? Problems = null)
{
    public static QuestionWriteResult Invalid(IReadOnlyList<ContentProblem> problems) => new(QuestionWriteStatus.Invalid, Problems: problems);
    public static QuestionWriteResult Duplicate => new(QuestionWriteStatus.Duplicate);
    public static QuestionWriteResult Oversize => new(QuestionWriteStatus.Oversize, Problems: [new("content.tooLarge", "The serialized card and reveal must not exceed 16 KB.")]);
    public static QuestionWriteResult Missing => new(QuestionWriteStatus.Missing);
}

public enum QuestionStatus
{
    Draft,
    Pending,
    Approved,
    Rejected,
}

public enum QuestionSource
{
    Seed,
    Import,
    Ai,
    Admin,
    Player,
}
