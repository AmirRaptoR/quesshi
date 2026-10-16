using Dapper;
using Npgsql;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Streaming;

/// <summary>Reads the global sign-in session state used to keep an SSE subscription authenticated.</summary>
internal interface IStreamSessionLiveness
{
    Task<IReadOnlySet<string>> LiveAsync(Guid tenantId, IReadOnlyCollection<string> sessionKeys, CancellationToken cancellationToken = default);
}

internal sealed class StreamSessionLiveness(DatabaseSources sources) : IStreamSessionLiveness
{
    public async Task<IReadOnlySet<string>> LiveAsync(Guid tenantId, IReadOnlyCollection<string> sessionKeys, CancellationToken cancellationToken = default)
    {
        if (sessionKeys.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var hashes = sessionKeys.Select(Convert.FromHexString).ToArray();
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        var live = await connection.QueryAsync<byte[]>(new CommandDefinition("""
            select token_hash
            from auth_sessions
            where tenant_id = @tenantId
              and token_hash = any(@hashes)
              and revoked_at is null
              and expires_at > now()
            """, new { tenantId, hashes }, cancellationToken: cancellationToken));
        return live.Select(Convert.ToHexString).ToHashSet(StringComparer.Ordinal);
    }
}
