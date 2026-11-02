using Quesshi.Plugin.Api;

namespace Quesshi.Application.Packs;

public enum PackVisibility
{
    Private,
    Unlisted,
    Tenant,
}

public static class PackVisibilities
{
    public static string ToValue(PackVisibility visibility) => visibility switch
    {
        PackVisibility.Private => "private",
        PackVisibility.Unlisted => "unlisted",
        PackVisibility.Tenant => "tenant",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Unknown pack visibility."),
    };

    public static bool TryParse(string? value, out PackVisibility visibility)
    {
        (var parsed, visibility) = value switch
        {
            "private" => (true, PackVisibility.Private),
            "unlisted" => (true, PackVisibility.Unlisted),
            "tenant" => (true, PackVisibility.Tenant),
            _ => (false, default(PackVisibility)),
        };
        return parsed;
    }
}

/// <summary>A pack. A null <paramref name="OwnerId"/> means the tenant owns it.</summary>
public sealed record Pack(
    Guid Id,
    Guid? OwnerId,
    string Title,
    string? Description,
    string Language,
    PackVisibility Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PackItem(Guid QuestionId, int Position);

/// <summary>Who is asking: <paramref name="PlayerId"/> is null for someone holding only a pack id.</summary>
public sealed record PackActor(Guid? PlayerId, bool IsTenantAdmin = false);

/// <summary>The one place that decides who may use or edit a pack; callers must not reimplement it.</summary>
public static class PackAccess
{
    public static bool CanUse(Pack pack, PackActor actor)
    {
        if (IsOwner(pack, actor)) return true;
        return pack.Visibility switch
        {
            PackVisibility.Unlisted => true,
            PackVisibility.Tenant => actor.PlayerId.HasValue,
            _ => false,
        };
    }

    public static bool CanEdit(Pack pack, PackActor actor) =>
        IsOwner(pack, actor) || (pack.OwnerId is null && actor.IsTenantAdmin);

    private static bool IsOwner(Pack pack, PackActor actor) =>
        pack.OwnerId.HasValue && pack.OwnerId == actor.PlayerId;
}

/// <summary>Stores packs inside a caller-owned tenant transaction. It does not check access; callers use <see cref="PackAccess"/>.</summary>
public interface IPacks
{
    Task<Pack> CreateAsync(ITenantTx transaction, Guid? ownerId, string title, string? description, string language, CancellationToken cancellationToken = default);
    Task<Pack?> GetAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default);
    Task<bool> RenameAsync(ITenantTx transaction, Guid id, string title, CancellationToken cancellationToken = default);
    Task<bool> DescribeAsync(ITenantTx transaction, Guid id, string? description, CancellationToken cancellationToken = default);
    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a value that is not a defined visibility.</summary>
    Task<bool> SetVisibilityAsync(ITenantTx transaction, Guid id, PackVisibility visibility, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Appends the question; false when the pack or question is missing or the question is already in the pack.</summary>
    Task<bool> AddQuestionAsync(ITenantTx transaction, Guid packId, Guid questionId, CancellationToken cancellationToken = default);
    /// <summary>Removes the question and closes the gap in the remaining positions.</summary>
    Task<bool> RemoveQuestionAsync(ITenantTx transaction, Guid packId, Guid questionId, CancellationToken cancellationToken = default);
    /// <summary>Rewrites positions 0..n-1 in the given order; false unless it names exactly the pack's current questions.</summary>
    Task<bool> ReorderAsync(ITenantTx transaction, Guid packId, IReadOnlyList<Guid> questionIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Pack>> ListOwnedAsync(ITenantTx transaction, Guid ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PackItem>> ListQuestionsAsync(ITenantTx transaction, Guid packId, CancellationToken cancellationToken = default);
}
