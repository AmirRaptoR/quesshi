using System.Text.Json;

namespace Quesshi.Application.Jobs;

/// <summary>Queues core background work with an explicit tenant scope.</summary>
internal interface ISystemJobs
{
    Task EnqueueForTenantAsync(
        Guid tenantId,
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey = null,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default);

    Task EnqueueGlobalAsync(
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey = null,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default);
}
