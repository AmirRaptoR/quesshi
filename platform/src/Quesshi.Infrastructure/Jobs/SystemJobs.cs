using System.Text.Json;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Jobs;

/// <summary>Queues system work without using ambient tenant context.</summary>
public sealed class SystemJobs : ISystemJobs, ISystemScheduledJobs
{
    private readonly ISystemDb _database;

    private const string Enqueue = """
        insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at)
        values (@tenantId, @owner, @kind, cast(@payload as jsonb), @uniqueKey, coalesce(@runAt, now()))
        on conflict do nothing
        """;

    private const string EnqueueScheduled = """
        with claimed as (
            insert into job_schedule_windows (tenant_id, owner, kind, interval_ticks, window_start)
            values (@tenantId, @owner, @kind, @intervalTicks, @window)
            on conflict do nothing
            returning 1
        ), enqueued as (
            insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at)
            select @tenantId, @owner, @kind, cast(@payload as jsonb), @uniqueKey, @window
            from claimed
            on conflict do nothing
            returning 1
        )
        select exists(select 1 from enqueued)
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

    async Task<bool> ISystemScheduledJobs.EnqueueOnceAsync(
        Guid tenantId,
        JobSchedule schedule,
        JsonElement payload,
        string uniqueKey,
        DateTimeOffset window,
        CancellationToken cancellationToken)
    {
        JobArguments.Validate(schedule.Owner, schedule.Kind, payload);
        return await _database.QuerySingleOrDefaultAsync<bool>(EnqueueScheduled, new
        {
            tenantId,
            schedule.Owner,
            schedule.Kind,
            intervalTicks = schedule.Interval.Ticks,
            payload = payload.GetRawText(),
            uniqueKey,
            window = window.ToUniversalTime()
        }, cancellationToken);
    }

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
