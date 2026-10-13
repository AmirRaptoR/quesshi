using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Quesshi.Plugin.Api;

/// <summary>Runs all tenant-owned data access within one locally scoped database transaction.</summary>
public interface ITenantDb
{
    Task<T> InTenantAsync<T>(
        Guid tenantId,
        Func<ITenantTx, Task<T>> callback,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);
}

/// <summary>Database operations bound to the tenant transaction supplied by <see cref="ITenantDb" />.</summary>
public interface ITenantTx
{
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Acquires shared rows in the platform's global transaction lock order.</summary>
public interface ILocks
{
    Task PlayersAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default);

    Task ContentAsync(ITenantTx transaction, IEnumerable<Guid> ids, CancellationToken cancellationToken = default);

    Task OwnAsync(ITenantTx transaction, string table, IEnumerable<Guid> keys, CancellationToken cancellationToken = default);
}

/// <summary>Collects inbox version bumps and applies them as the final transaction operation.</summary>
public interface IInbox
{
    void Changed(ITenantTx transaction, Guid playerId);
}

/// <summary>Raised in development and test environments when declared locks are requested out of order.</summary>
public sealed class LockOrderViolation(string message) : InvalidOperationException(message);

/// <summary>A JSONB value with explicit source-generated serialization metadata.</summary>
public readonly record struct Jsonb<T>(T Value, JsonTypeInfo<T> TypeInfo) : SqlMapper.ICustomQueryParameter
{
    public void AddParameter(IDbCommand command, string name)
    {
        var parameter = (NpgsqlParameter)command.CreateParameter();
        parameter.ParameterName = name;
        parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        parameter.Value = JsonSerializer.SerializeToDocument(Value, TypeInfo);
        command.Parameters.Add(parameter);
    }
}
