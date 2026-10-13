using System.Text.RegularExpressions;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Database;

/// <summary>Implements the platform-wide explicit lock order for tenant transactions.</summary>
public sealed partial class TenantLocks(bool enforceOrder = true) : ILocks
{
    public async Task PlayersAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var tenantTransaction = GetTransaction(transaction);
        var keys = DistinctOrdered(ids);
        EnsureStep(tenantTransaction, 1, "players", keys, ["players"]);
        var locked = await transaction.QueryAsync<Guid>(
            "select id from players where tenant_id = @tenantId and id = any(@ids) order by id for no key update",
            new { tenantId = tenantTransaction.TenantId, ids = keys },
            cancellationToken);
        Remember(tenantTransaction, "players", locked);
        tenantTransaction.LastLockStep = Math.Max(tenantTransaction.LastLockStep, 1);
    }

    public async Task ContentAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var tenantTransaction = GetTransaction(transaction);
        var keys = DistinctOrdered(ids);
        EnsureStep(tenantTransaction, 2, "content", keys, ["questions", "packs"]);
        var parameters = new { tenantId = tenantTransaction.TenantId, ids = keys };
        var questions = await transaction.QueryAsync<Guid>(
            "select id from questions where tenant_id = @tenantId and id = any(@ids) order by id for update",
            parameters,
            cancellationToken);
        var packs = await transaction.QueryAsync<Guid>(
            "select id from packs where tenant_id = @tenantId and id = any(@ids) order by id for update",
            parameters,
            cancellationToken);
        Remember(tenantTransaction, "questions", questions);
        Remember(tenantTransaction, "packs", packs);
        tenantTransaction.LastLockStep = Math.Max(tenantTransaction.LastLockStep, 2);
    }

    public async Task OwnAsync(ITenantTx transaction, string table, IEnumerable<Guid> keys, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (!TableName().IsMatch(table)) throw new ArgumentException("The table name is not a valid plugin table identifier.", nameof(table));

        var tenantTransaction = GetTransaction(transaction);
        var orderedKeys = DistinctOrdered(keys);
        var owner = await transaction.QuerySingleOrDefaultAsync<string>(
            "select obj_description(@table::regclass)",
            new { table },
            cancellationToken);
        if (owner is null || owner == "owner:core" || !owner.StartsWith("owner:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only registered plugin-owned tables can be locked through OwnAsync.", nameof(table));
        }

        EnsureStep(tenantTransaction, 3, table, orderedKeys, [table]);
        var locked = await transaction.QueryAsync<Guid>(
            $"select id from public.\"{table}\" where tenant_id = @tenantId and id = any(@ids) order by id for update",
            new { tenantId = tenantTransaction.TenantId, ids = orderedKeys },
            cancellationToken);
        Remember(tenantTransaction, table, locked);
        tenantTransaction.LastLockStep = Math.Max(tenantTransaction.LastLockStep, 3);
    }

    private void EnsureStep(TenantTransaction transaction, int requestedStep, string name, IReadOnlyCollection<Guid> keys, IReadOnlyCollection<string> tables)
    {
        if (!enforceOrder || requestedStep >= transaction.LastLockStep || AllHeld(transaction, tables, keys)) return;
        throw new LockOrderViolation($"Cannot lock {name} at step {requestedStep} after step {transaction.LastLockStep}.");
    }

    private static bool AllHeld(TenantTransaction transaction, IEnumerable<string> tables, IEnumerable<Guid> keys) =>
        keys.All(key => tables.Any(table => transaction.HeldLocks.Contains((table, key))));

    private static TenantTransaction GetTransaction(ITenantTx transaction) => transaction as TenantTransaction
        ?? throw new ArgumentException("The transaction was not created by ITenantDb.", nameof(transaction));

    private static Guid[] DistinctOrdered(IEnumerable<Guid> ids) => ids.Distinct().Order().ToArray();

    private static void Remember(TenantTransaction transaction, string table, IEnumerable<Guid> ids)
    {
        foreach (var id in ids) transaction.HeldLocks.Add((table, id));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableName();
}
