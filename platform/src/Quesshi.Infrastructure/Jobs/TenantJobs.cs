using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Jobs;

/// <summary>Queues work through the tenant-bound database function.</summary>
public sealed class TenantJobs : IJobs
{
    public async Task EnqueueAsync(
        ITenantTx transaction,
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey = null,
        DateTimeOffset? runAt = null,
        int? maxAttempts = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        JobArguments.Validate(owner, kind, payload);

        await transaction.ExecuteAsync(
            "select enqueue_job(@owner, @kind, cast(@payload as jsonb), @uniqueKey, coalesce(@runAt, now()), @maxAttempts)",
            new { owner, kind, payload = payload.GetRawText(), uniqueKey, runAt = runAt?.ToUniversalTime(), maxAttempts },
            cancellationToken);
    }
}
