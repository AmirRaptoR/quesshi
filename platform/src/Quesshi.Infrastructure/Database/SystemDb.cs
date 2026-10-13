using Dapper;
using Npgsql;

namespace Quesshi.Infrastructure.Database;

/// <summary>Core-only global-table operations. It is intentionally not part of Plugin.Api.</summary>
internal interface ISystemDb
{
    Task<T> InTransactionAsync<T>(Func<ISystemTx, Task<T>> callback, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
}

internal interface ISystemTx
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
}

internal sealed class SystemDb(DatabaseSources sources) : ISystemDb
{
    static SystemDb() => DapperConventions.Configure();

    public async Task<T> InTransactionAsync<T>(Func<ISystemTx, Task<T>> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        await using var connection = await sources.System.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var systemTransaction = new SystemTransaction(connection, transaction);
        try
        {
            var result = await callback(systemTransaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await sources.System.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await sources.System.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await sources.System.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }
}

internal sealed class SystemTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : ISystemTx
{
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var rows = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
}
