using Quesshi.Application;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Streaming;

/// <summary>Publishes committed session-effect revisions to the stream listener.</summary>
internal sealed class PostgresSessionEffectsNotifier : ISessionEffectsNotifier
{
    public Task ChangedAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken = default) =>
        transaction.ExecuteAsync("""
            select pg_notify('quesshi_effects', concat('effects:', tenant_id, ':', session_id, ':', revision))
            from session_effects
            where session_id = @sessionId
            """, new { sessionId }, cancellationToken);
}
