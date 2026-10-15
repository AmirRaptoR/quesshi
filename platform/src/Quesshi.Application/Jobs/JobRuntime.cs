using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Jobs;

internal delegate Task JobHandler(JobContext context, CancellationToken cancellationToken);

internal sealed record JobHandlerRegistration(string Owner, string Kind, JobHandler Handler, WhenDisabled WhenDisabled);

/// <summary>Registers exactly one handler for each durable job owner and kind.</summary>
internal sealed class JobHandlerRegistry
{
    private readonly Dictionary<(string Owner, string Kind), JobHandlerRegistration> _handlers = new();

    public void Register(string owner, string kind, JobHandler handler, WhenDisabled whenDisabled = WhenDisabled.Complete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(whenDisabled)) throw new ArgumentOutOfRangeException(nameof(whenDisabled));

        var key = (owner.Trim(), kind.Trim());
        if (!_handlers.TryAdd(key, new JobHandlerRegistration(key.Item1, key.Item2, handler, whenDisabled)))
        {
            throw new InvalidOperationException($"A handler is already registered for job '{key.Item1}/{key.Item2}'.");
        }
    }

    public bool TryGet(string owner, string kind, out JobHandlerRegistration registration) =>
        _handlers.TryGetValue((owner, kind), out registration!);
}

internal sealed record LeasedJob(
    long Id,
    Guid? TenantId,
    string Owner,
    string Kind,
    JsonElement Payload,
    Guid LockedBy,
    DateTimeOffset LockedUntil,
    int Attempts,
    int? MaxAttempts);

internal sealed record DeadJob(long Id, Guid? TenantId, string Owner, string Kind, int Attempts, string? LastError);

/// <summary>System-role durable-job state transitions. It is intentionally not part of Plugin.Api.</summary>
internal interface IJobStore
{
    Task<IReadOnlyList<LeasedJob>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default);
    Task<bool> FailAsync(long id, Guid lockedBy, int attempts, int? maxAttempts, string error, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeadJob>> ListDeadAsync(CancellationToken cancellationToken = default);
    Task<bool> RerunDeadAsync(long id, CancellationToken cancellationToken = default);
}

internal sealed class JobWorker(
    IJobStore jobs,
    JobHandlerRegistry handlers,
    ITenantDb tenantDatabase,
    ICapabilities capabilities,
    ILogger<JobWorker>? logger = null)
{
    private readonly ILogger<JobWorker> _logger = logger ?? NullLogger<JobWorker>.Instance;
    internal const int MaximumConcurrentHandlers = 4;

    private readonly SemaphoreSlim _slots = new(MaximumConcurrentHandlers, MaximumConcurrentHandlers);

    /// <summary>Claims up to the free handler slots and runs the jobs, returning when all of them have finished.</summary>
    public async Task<int> RunPassAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var running = await StartPassAsync(batchSize, leaseDuration, cancellationToken);
        await Task.WhenAll(running);
        return running.Count;
    }

    /// <summary>
    /// Claims up to the free handler slots and starts the jobs without awaiting them, so a long handler
    /// never stalls the polling loop. Each returned task completes when its job's bookkeeping is done.
    /// </summary>
    public async Task<IReadOnlyList<Task>> StartPassAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var free = Math.Min(batchSize, _slots.CurrentCount);
        if (free == 0) return [];

        var claimed = await jobs.ClaimAsync(free, leaseDuration, cancellationToken);
        var running = new List<Task>(claimed.Count);
        foreach (var job in claimed)
        {
            await _slots.WaitAsync(CancellationToken.None);
            running.Add(Task.Run(() => ExecuteGuardedAsync(job, cancellationToken), CancellationToken.None));
        }

        return running;
    }

    private async Task ExecuteGuardedAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(job, cancellationToken);
        }
        catch
        {
            // Bookkeeping failed or the host is stopping; the lease expires and the job is claimed again.
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task ExecuteAsync(LeasedJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (!handlers.TryGet(job.Owner, job.Kind, out var handler))
            {
                await jobs.FailAsync(job.Id, job.LockedBy, job.Attempts, job.MaxAttempts, $"No handler is registered for job '{job.Owner}/{job.Kind}'.", cancellationToken);
                return;
            }

            if (job.TenantId is { } tenantId && job.Owner != "core"
                && handler.WhenDisabled == WhenDisabled.Cancel
                && !await capabilities.IsOnAsync(tenantId, CapabilityType.Feature, job.Owner, cancellationToken))
            {
                await jobs.CancelAsync(job.Id, job.LockedBy, cancellationToken);
                return;
            }

            ITenantDb? boundDatabase = job.TenantId is { } tenant
                ? new JobTenantDb(tenantDatabase, tenant)
                : null;
            await handler.Handler(new JobContext(job.Id, job.TenantId, job.Owner, job.Kind, job.Payload, boundDatabase), cancellationToken);
            await jobs.CompleteAsync(job.Id, job.LockedBy, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await jobs.FailAsync(job.Id, job.LockedBy, job.Attempts, job.MaxAttempts, exception.ToString(), cancellationToken);
            // Unlimited jobs never transition to dead, so surface every failure rather than leaving
            // an indefinitely retrying cleanup visible only in the jobs table.
            if (job.MaxAttempts is null)
            {
                _logger.LogError(exception,
                    "Unlimited durable job {Owner}/{Kind} ({JobId}) failed on attempt {Attempt} for tenant {TenantId}",
                    job.Owner, job.Kind, job.Id, job.Attempts, job.TenantId);
            }
        }
    }

    private sealed class JobTenantDb(ITenantDb inner, Guid tenantId) : ITenantDb
    {
        public Task<T> InTenantAsync<T>(
            Guid requestedTenantId,
            Func<ITenantTx, Task<T>> callback,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default,
            bool readOnly = false)
        {
            if (requestedTenantId != tenantId)
            {
                throw new InvalidOperationException("A job handler can only access its job tenant.");
            }

            return inner.InTenantAsync(tenantId, callback, isolationLevel, cancellationToken, readOnly);
        }
    }
}
