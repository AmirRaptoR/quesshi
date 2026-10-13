using System.Text.Json;

namespace Quesshi.Plugin.Api;

/// <summary>Queues tenant-owned background work in the transaction that produced it.</summary>
public interface IJobs
{
    Task EnqueueAsync(
        ITenantTx transaction,
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey = null,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default);
}
