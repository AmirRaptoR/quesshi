using System.Text.Json;

namespace Quesshi.Plugin.Api;

/// <summary>A pluggable, tenant-scoped source of questions for a session.</summary>
public interface ISource
{
    string Id { get; }

    /// <summary>The concrete record accepted in the JSON parameter boundary.</summary>
    Type Params { get; }

    /// <summary>Settings stored in this source's tenant capability row.</summary>
    FeatureSettings Settings { get; }

    /// <summary>Performs an early access check only. <see cref="PickAsync"/> must check again.</summary>
    Task<bool> AuthorizeAsync(ITenantTx transaction, JsonElement parameters, Guid actor, CancellationToken cancellationToken = default);

    /// <summary>Draws items inside the transaction that will consume them.</summary>
    Task<Picked> PickAsync(ITenantTx transaction, JsonElement parameters, SourceDrawPlan plan, Guid actor, CancellationToken cancellationToken = default);
}

public interface ISourceRegistry
{
    ISource Get(string id);
    bool TryGet(string id, out ISource? source);
}

/// <summary>The format-owned constraints for a source draw.</summary>
public sealed record SourceDrawPlan(
    int Count,
    string Language,
    IReadOnlyList<SourceKind> AcceptedKinds,
    IReadOnlyList<int>? Levels,
    long Seed);

/// <summary>A kind accepted by the format and its stable content family.</summary>
public sealed record SourceKind(string Id, string Family);

public abstract record Picked;

public sealed record PickedQuestionIds(IReadOnlyList<Guid> Ids) : Picked;

public sealed record PickedSnapshots(IReadOnlyList<ReadySnapshot> Items) : Picked;

/// <summary>A fully resolved item supplied by a source that does not originate in the question bank.</summary>
public sealed record ReadySnapshot(
    string Kind,
    int KindVersion,
    string Family,
    int? Level,
    Guid? CategoryId,
    Guid? MediaId,
    string Prompt,
    JsonElement Payload,
    JsonElement Resolved,
    JsonElement Card,
    JsonElement Reveal);

public sealed class SourceUnavailableException : InvalidOperationException
{
    public SourceUnavailableException() : base("source_unavailable") { }
}

public sealed class InvalidSourceResultException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class NotEnoughQuestionsException(int found, int needed)
    : InvalidOperationException($"not_enough_questions: found {found} of {needed}")
{
    public int Found { get; } = found;
    public int Needed { get; } = needed;
}

public sealed class SourceParameterException(string message) : InvalidOperationException(message);
