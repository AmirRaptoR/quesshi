using System.Text.Json;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Jobs;

/// <summary>Queues system work without using ambient tenant context.</summary>
public sealed class SystemJobs : ISystemJobs
{
    private readonly ISystemDb _database;

    private const string Enqueue = """
        insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at)
        values (@tenantId, @owner, @kind, cast(@payload as jsonb), @uniqueKey, coalesce(@runAt, now()))
        on conflict do nothing
        """;

    public SystemJobs(DatabaseSources sources)
        : this(new SystemDb(sources))
    {
    }

    internal SystemJobs(ISystemDb database) => _database = database;

    public Task EnqueueForTenantAsync(
        Guid tenantId,
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey = null,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(tenantId, owner, kind, payload, uniqueKey, runAt, cancellationToken);

    public Task EnqueueGlobalAsync(
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey = null,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(null, owner, kind, payload, uniqueKey, runAt, cancellationToken);

    private async Task EnqueueAsync(
        Guid? tenantId,
        string owner,
        string kind,
        JsonElement payload,
        string? uniqueKey,
        DateTimeOffset? runAt,
        CancellationToken cancellationToken)
    {
        JobArguments.Validate(owner, kind, payload);
        await _database.ExecuteAsync(Enqueue, new { tenantId, owner, kind, payload = payload.GetRawText(), uniqueKey, runAt = runAt?.ToUniversalTime() }, cancellationToken);
    }
}
