using System.Text.Json;
using Quesshi.Application.Packs;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Sources.Pack;

/// <summary>Draws a pack's questions in position order. Access is decided by <see cref="PackAccess"/> under the pack row lock.</summary>
public sealed class PackSource(IPacks packs, ILocks locks) : ISource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string SourceId = "pack";

    public string Id => SourceId;

    public Type Params => typeof(PackParams);

    public FeatureSettings Settings { get; } = FeatureSettings.Create(new PackSettings(), _ => []);

    public async Task<bool> AuthorizeAsync(ITenantTx transaction, JsonElement parameters, Guid actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var pack = await packs.GetAsync(transaction, ReadParameters(parameters).PackId, cancellationToken);
        return pack is not null && PackAccess.CanUse(pack, ActorOf(actor));
    }

    public async Task<Picked> PickAsync(ITenantTx transaction, JsonElement parameters, SourceDrawPlan plan, Guid actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count <= 0) throw new SourceParameterException("The draw count must be positive.");
        if (plan.AcceptedKinds.Count == 0) throw new SourceParameterException("At least one accepted kind is required.");

        var packId = ReadParameters(parameters).PackId;
        // Content locks are ascending within a step and the session factory locks the drawn questions afterwards, so the
        // pack and its questions are locked together here. The unlocked read only names which rows to lock.
        var seen = await packs.ListQuestionsAsync(transaction, packId, cancellationToken);
        await locks.ContentAsync(transaction, seen.Select(item => item.QuestionId).Append(packId), cancellationToken);
        var pack = await packs.GetAsync(transaction, packId, cancellationToken);
        if (pack is null || !PackAccess.CanUse(pack, ActorOf(actor))) throw new SourceUnavailableException();

        var items = await packs.ListQuestionsAsync(transaction, packId, cancellationToken);
        // A question added between the unlocked read and the lock is not locked; the caller retries the start.
        if (items.Any(item => seen.All(known => known.QuestionId != item.QuestionId))) throw new SourceUnavailableException();
        var kinds = await transaction.QueryAsync<QuestionKind>(
            "select id as Id, kind as Kind from questions where id = any(@ids)",
            new { ids = items.Select(item => item.QuestionId).ToArray() },
            cancellationToken);
        var accepted = plan.AcceptedKinds.Select(kind => kind.Id).ToHashSet(StringComparer.Ordinal);
        var acceptable = kinds.Where(question => accepted.Contains(question.Kind)).Select(question => question.Id).ToHashSet();
        var picked = items.OrderBy(item => item.Position)
            .Where(item => acceptable.Contains(item.QuestionId))
            .Take(plan.Count)
            .Select(item => item.QuestionId)
            .ToArray();
        if (picked.Length < plan.Count) throw new NotEnoughQuestionsException(picked.Length, plan.Count);
        return new PickedQuestionIds(picked);
    }

    /// <summary>A zero actor is someone holding only a pack id, not a signed-in player.</summary>
    private static PackActor ActorOf(Guid actor) => new(actor == Guid.Empty ? null : actor);

    private static PackParams ReadParameters(JsonElement parameters)
    {
        if (parameters.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new SourceParameterException("The pack parameters are required.");
        PackParams? read;
        try { read = JsonSerializer.Deserialize<PackParams>(parameters, JsonOptions); }
        catch (JsonException) { throw new SourceParameterException("The pack parameters are invalid."); }
        return read is { PackId: var id } && id != Guid.Empty ? read : throw new SourceParameterException("The pack parameters are invalid.");
    }

    private sealed record QuestionKind(Guid Id, string Kind);
}

public sealed record PackParams(Guid PackId);

public sealed record PackSettings;
