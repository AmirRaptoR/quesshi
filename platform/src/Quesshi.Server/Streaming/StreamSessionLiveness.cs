using Dapper;
using Npgsql;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Streaming;

/// <summary>Reads the global sign-in session state used to keep an SSE subscription authenticated.</summary>
internal interface IStreamSessionLiveness
{
    Task<IReadOnlyDictionary<string, StreamSessionStatus>> StatusAsync(Guid tenantId, IReadOnlyCollection<string> sessionKeys,
        CancellationToken cancellationToken = default);
}

internal enum StreamSessionStatus { Live, SignedOut, Expired }

internal sealed class StreamSessionLiveness(DatabaseSources sources) : IStreamSessionLiveness
{
    public async Task<IReadOnlyDictionary<string, StreamSessionStatus>> StatusAsync(Guid tenantId, IReadOnlyCollection<string> sessionKeys,
        CancellationToken cancellationToken = default)
    {
        var statuses = sessionKeys.ToDictionary(key => key, _ => StreamSessionStatus.SignedOut, StringComparer.Ordinal);
        if (sessionKeys.Count == 0) return statuses;

        var hashes = sessionKeys.Select(Convert.FromHexString).ToArray();
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        var sessions = await connection.QueryAsync<SessionState>(new CommandDefinition("""
            select token_hash as TokenHash, revoked_at as RevokedAt, expires_at <= now() as IsExpired
            from auth_sessions
            where tenant_id = @tenantId
              and token_hash = any(@hashes)
            """, new { tenantId, hashes }, cancellationToken: cancellationToken));
        foreach (var session in sessions)
        {
            statuses[Convert.ToHexString(session.TokenHash)] = session.IsExpired
                ? StreamSessionStatus.Expired
                : session.RevokedAt is null ? StreamSessionStatus.Live : StreamSessionStatus.SignedOut;
        }
        return statuses;
    }

    private sealed record SessionState(byte[] TokenHash, DateTimeOffset? RevokedAt, bool IsExpired);
}
