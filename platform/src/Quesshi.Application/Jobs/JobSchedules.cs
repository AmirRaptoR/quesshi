namespace Quesshi.Application.Jobs;

/// <summary>A recurring declaration for one registered tenant job handler.</summary>
internal sealed record JobSchedule(string Owner, string Kind, TimeSpan Interval);

/// <summary>
/// Holds the recurring schedule declarations made by the core job-registration boundary.
/// Feature builders can delegate their <c>Every</c> declarations here without owning scheduling rules.
/// </summary>
internal sealed class JobScheduleRegistry(JobHandlerRegistry handlers)
{
    private readonly Dictionary<(string Owner, string Kind, TimeSpan Interval), JobSchedule> _schedules = [];

    public IReadOnlyCollection<JobSchedule> Schedules => _schedules.Values;

    public void Every(string owner, TimeSpan interval, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        var normalizedOwner = owner.Trim();
        var normalizedKind = kind.Trim();
        if (!handlers.TryGet(normalizedOwner, normalizedKind, out _))
        {
            throw new InvalidOperationException($"A handler must be registered before scheduling job '{normalizedOwner}/{normalizedKind}'.");
        }

        var key = (normalizedOwner, normalizedKind, interval);
        if (!_schedules.TryAdd(key, new JobSchedule(normalizedOwner, normalizedKind, interval)))
        {
            throw new InvalidOperationException($"Job '{normalizedOwner}/{normalizedKind}' is already scheduled every {interval}.");
        }
    }
}

/// <summary>Finds tenants that have enabled the feature which owns a scheduled job.</summary>
internal interface IJobScheduleTenants
{
    Task<IReadOnlyList<Guid>> ListEnabledAsync(string owner, CancellationToken cancellationToken = default);
}

/// <summary>Atomically claims a schedule window and queues its system job once.</summary>
internal interface ISystemScheduledJobs
{
    Task<bool> EnqueueOnceAsync(
        Guid tenantId,
        JobSchedule schedule,
        System.Text.Json.JsonElement payload,
        string uniqueKey,
        DateTimeOffset window,
        CancellationToken cancellationToken = default);
}

/// <summary>Enqueues the current UTC window for every enabled tenant schedule.</summary>
internal sealed class JobScheduler(
    JobScheduleRegistry schedules,
    IJobScheduleTenants tenants,
    ISystemScheduledJobs jobs,
    TimeProvider? timeProvider = null)
{
    private static readonly System.Text.Json.JsonElement EmptyPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { });
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<int> RunPassAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var enqueued = 0;
        foreach (var schedule in schedules.Schedules)
        {
            var window = WindowStart(now, schedule.Interval);
            var uniqueKey = UniqueKey(schedule, window);
            var enabledTenants = await tenants.ListEnabledAsync(schedule.Owner, cancellationToken);
            foreach (var tenantId in enabledTenants)
            {
                if (await jobs.EnqueueOnceAsync(
                    tenantId,
                    schedule,
                    EmptyPayload,
                    uniqueKey,
                    window,
                    cancellationToken))
                {
                    enqueued++;
                }
            }
        }

        return enqueued;
    }

    internal static DateTimeOffset WindowStart(DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var utc = now.ToUniversalTime();
        var ticks = utc.UtcDateTime.Ticks - utc.UtcDateTime.Ticks % interval.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    internal static string UniqueKey(JobSchedule schedule, DateTimeOffset window) =>
        $"schedule:{schedule.Owner}:{schedule.Kind}:{schedule.Interval.Ticks}:{window.UtcDateTime.Ticks}";
}
