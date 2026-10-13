using Dapper;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Jobs;

/// <summary>Reads enabled feature owners through the system database role.</summary>
public sealed class PostgresJobScheduleTenants(DatabaseSources sources) : IJobScheduleTenants
{
    public async Task<IReadOnlyList<Guid>> ListEnabledAsync(string owner, CancellationToken cancellationToken = default)
    {
        await using var connection = await sources.System.OpenConnectionAsync(cancellationToken);
        var command = new CommandDefinition("""
            select tenant_id
            from tenant_capabilities
            where type = 'feature' and key = @owner and entitled and enabled
            """, new { owner }, cancellationToken: cancellationToken);
        var tenants = await connection.QueryAsync<Guid>(command);
        return tenants.AsList();
    }
}
