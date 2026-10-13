using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Database;

public sealed class TenantDb(DatabaseSources sources, IInboxBumpSink? inboxBumpSink = null) : ITenantDb
{
    private const int MaximumAttempts = 3;
    private readonly IInboxBumpSink _inboxBumpSink = inboxBumpSink ?? new PlayerInboxBumpSink();

    static TenantDb() => DapperConventions.Configure();

    public async Task<T> InTenantAsync<T>(
        Guid tenantId,
        Func<ITenantTx, Task<T>> callback,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunAttemptAsync(tenantId, callback, isolationLevel, cancellationToken);
            }
            catch (PostgresException exception) when (IsTransient(exception) && attempt < MaximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken);
            }
        }
    }

    private async Task<T> RunAttemptAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        await using var connection = await sources.App.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
        var tenantTransaction = new TenantTransaction(connection, transaction, tenantId, _inboxBumpSink);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select set_config('app.tenant_id', @tenantId, true)",
                new { tenantId = tenantId.ToString() },
                transaction,
                cancellationToken: cancellationToken));
            var result = await callback(tenantTransaction);
            await tenantTransaction.FlushInboxAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool IsTransient(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
}

internal sealed class TenantTransaction(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    Guid tenantId,
    IInboxBumpSink inboxBumpSink) : ITenantTx
{
    private readonly SortedSet<Guid> _inboxChanges = [];

    internal Guid TenantId { get; } = tenantId;

    internal int LastLockStep { get; set; }

    internal HashSet<(string Table, Guid Key)> HeldLocks { get; } = [];

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var rows = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));

    internal void RecordInboxChange(Guid playerId) => _inboxChanges.Add(playerId);

    internal async Task FlushInboxAsync(CancellationToken cancellationToken)
    {
        foreach (var playerId in _inboxChanges)
        {
            await inboxBumpSink.ApplyAsync(this, playerId, cancellationToken);
        }
    }
}
