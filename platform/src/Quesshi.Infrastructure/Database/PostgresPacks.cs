using Quesshi.Application.Packs;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Database;

/// <summary>Dapper pack store. Item changes lock the pack row first, so concurrent edits queue instead of colliding on positions.</summary>
public sealed class PostgresPacks(ILocks locks) : IPacks
{
    private const string Columns = "id as Id, owner_id as OwnerId, title as Title, description as Description, lang as Language, visibility as Visibility, created_at as CreatedAt, updated_at as UpdatedAt";

    public async Task<Pack> CreateAsync(ITenantTx transaction, Guid? ownerId, string title, string? description, string language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var row = await transaction.QuerySingleOrDefaultAsync<PackRow>($"""
            insert into packs (tenant_id, id, owner_id, title, description, lang)
            values (current_setting('app.tenant_id')::uuid, @id, @ownerId, @title, @description, @language)
            returning {Columns}
            """, new { id = Guid.NewGuid(), ownerId, title = title.Trim(), description = NullIfBlank(description), language = language.Trim() }, cancellationToken);
        return row!.ToPack();
    }

    public async Task<Pack?> GetAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default) =>
        (await transaction.QuerySingleOrDefaultAsync<PackRow>($"select {Columns} from packs where id = @id", new { id }, cancellationToken))?.ToPack();

    public async Task<bool> RenameAsync(ITenantTx transaction, Guid id, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return await transaction.ExecuteAsync("update packs set title = @title, updated_at = now() where id = @id", new { id, title = title.Trim() }, cancellationToken) != 0;
    }

    public async Task<bool> DescribeAsync(ITenantTx transaction, Guid id, string? description, CancellationToken cancellationToken = default) =>
        await transaction.ExecuteAsync("update packs set description = @description, updated_at = now() where id = @id", new { id, description = NullIfBlank(description) }, cancellationToken) != 0;

    public async Task<bool> SetVisibilityAsync(ITenantTx transaction, Guid id, PackVisibility visibility, CancellationToken cancellationToken = default) =>
        await transaction.ExecuteAsync("update packs set visibility = @visibility, updated_at = now() where id = @id", new { id, visibility = PackVisibilities.ToValue(visibility) }, cancellationToken) != 0;

    public async Task<bool> DeleteAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default) =>
        await transaction.ExecuteAsync("delete from packs where id = @id", new { id }, cancellationToken) != 0;

    public async Task<bool> AddQuestionAsync(ITenantTx transaction, Guid packId, Guid questionId, CancellationToken cancellationToken = default)
    {
        await locks.ContentAsync(transaction, [packId, questionId], cancellationToken);
        return await transaction.ExecuteAsync("""
            insert into pack_items (tenant_id, pack_id, question_id, position)
            select current_setting('app.tenant_id')::uuid, p.id, q.id,
                   coalesce((select max(position) + 1 from pack_items where pack_id = p.id), 0)
            from packs p, questions q
            where p.id = @packId and q.id = @questionId
            on conflict (tenant_id, pack_id, question_id) do nothing
            """, new { packId, questionId }, cancellationToken) != 0;
    }

    public async Task<bool> RemoveQuestionAsync(ITenantTx transaction, Guid packId, Guid questionId, CancellationToken cancellationToken = default)
    {
        await locks.ContentAsync(transaction, [packId], cancellationToken);
        if (await transaction.ExecuteAsync("delete from pack_items where pack_id = @packId and question_id = @questionId", new { packId, questionId }, cancellationToken) == 0)
            return false;
        await transaction.ExecuteAsync("""
            update pack_items item set position = ranked.position
            from (select question_id, (row_number() over (order by position) - 1)::int as position
                  from pack_items where pack_id = @packId) ranked
            where item.pack_id = @packId and item.question_id = ranked.question_id and item.position <> ranked.position
            """, new { packId }, cancellationToken);
        return true;
    }

    public async Task<bool> ReorderAsync(ITenantTx transaction, Guid packId, IReadOnlyList<Guid> questionIds, CancellationToken cancellationToken = default)
    {
        await locks.ContentAsync(transaction, [packId], cancellationToken);
        var current = await transaction.QueryAsync<Guid>("select question_id from pack_items where pack_id = @packId", new { packId }, cancellationToken);
        if (questionIds.Count != current.Count || questionIds.Distinct().Count() != questionIds.Count || !current.ToHashSet().SetEquals(questionIds))
            return false;
        // The unique position constraint is deferred, so rewriting every row in one statement is safe.
        await transaction.ExecuteAsync("""
            update pack_items item set position = (ordered.position - 1)::int
            from unnest(@ids::uuid[]) with ordinality as ordered(question_id, position)
            where item.pack_id = @packId and item.question_id = ordered.question_id
            """, new { packId, ids = questionIds.ToArray() }, cancellationToken);
        await transaction.ExecuteAsync("update packs set updated_at = now() where id = @packId", new { packId }, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Pack>> ListOwnedAsync(ITenantTx transaction, Guid ownerId, CancellationToken cancellationToken = default) =>
        (await transaction.QueryAsync<PackRow>($"select {Columns} from packs where owner_id = @ownerId order by created_at, id", new { ownerId }, cancellationToken))
            .Select(row => row.ToPack()).ToArray();

    public async Task<IReadOnlyList<PackItem>> ListQuestionsAsync(ITenantTx transaction, Guid packId, CancellationToken cancellationToken = default) =>
        await transaction.QueryAsync<PackItem>("select question_id as QuestionId, position as Position from pack_items where pack_id = @packId order by position", new { packId }, cancellationToken);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PackRow(Guid Id, Guid? OwnerId, string Title, string? Description, string Language, string Visibility, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public Pack ToPack() => new(Id, OwnerId, Title, Description, Language,
            PackVisibilities.TryParse(Visibility, out var visibility) ? visibility : throw new InvalidOperationException($"Unknown stored pack visibility '{Visibility}'."),
            CreatedAt, UpdatedAt);
    }
}
