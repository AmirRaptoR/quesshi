using Dapper;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Jobs;

/// <summary>System-role lease and state operations for durable jobs.</summary>
internal sealed class PostgresJobStore(ISystemDb database) : IJobStore
{
    private const string Claim = """
        with due as (
            select id
            from jobs
            where status = 'ready'
              and run_at <= now()
              and (locked_until is null or locked_until <= now())
            order by run_at, id
            limit @batchSize
            for update skip locked
        )
        update jobs
        set locked_by = @lockedBy,
            locked_until = now() + @leaseDuration,
            attempts = attempts + 1
        from due
        where jobs.id = due.id
        returning jobs.id as Id,
                  jobs.tenant_id as TenantId,
                  jobs.owner as Owner,
                  jobs.kind as Kind,
                  jobs.payload as Payload,
                  jobs.locked_by as LockedBy,
                  jobs.locked_until as LockedUntil,
                  jobs.attempts as Attempts,
                  jobs.max_attempts as MaxAttempts
        """;

    private const string Complete = """
        update jobs
        set status = 'done', locked_by = null, locked_until = null, finished_at = now(), last_error = null
        where id = @id and status = 'ready' and locked_by = @lockedBy
        """;

    private const string Fail = """
        update jobs
        set status = case when max_attempts is not null and attempts >= max_attempts then 'dead' else 'ready' end,
            run_at = case when max_attempts is null or attempts < max_attempts then now() + @retryDelay else run_at end,
            locked_by = null,
            locked_until = null,
            last_error = @error,
            finished_at = case when max_attempts is not null and attempts >= max_attempts then now() else null end
        where id = @id and status = 'ready' and locked_by = @lockedBy
        """;

    private const string Cancel = """
        update jobs
        set status = 'cancelled', locked_by = null, locked_until = null, finished_at = now()
        where id = @id and status = 'ready' and locked_by = @lockedBy
        """;

    public PostgresJobStore(DatabaseSources sources) : this(new SystemDb(sources)) { }

    public async Task<IReadOnlyList<LeasedJob>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        return await database.QueryAsync<LeasedJob>(Claim, new { batchSize, lockedBy = Guid.NewGuid(), leaseDuration }, cancellationToken);
    }

    public Task<bool> CompleteAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) =>
        UpdateAsync(Complete, new { id, lockedBy }, cancellationToken);

    public Task<bool> FailAsync(long id, Guid lockedBy, int attempts, int? maxAttempts, string error, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return UpdateAsync(Fail, new { id, lockedBy, retryDelay = RetryDelay(attempts), error }, cancellationToken);
    }

    public Task<bool> CancelAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) =>
        UpdateAsync(Cancel, new { id, lockedBy }, cancellationToken);

    public Task<IReadOnlyList<DeadJob>> ListDeadAsync(CancellationToken cancellationToken = default) =>
        database.QueryAsync<DeadJob>("""
            select id as Id, tenant_id as TenantId, owner as Owner, kind as Kind, attempts as Attempts, last_error as LastError
            from jobs
            where status = 'dead'
            order by finished_at, id
            """, cancellationToken: cancellationToken);

    public Task<bool> RerunDeadAsync(long id, CancellationToken cancellationToken = default) =>
        UpdateAsync("""
            update jobs
            set status = 'ready', run_at = now(), locked_by = null, locked_until = null, finished_at = null
            where id = @id and status = 'dead'
            """, new { id }, cancellationToken);

    private async Task<bool> UpdateAsync(string sql, object parameters, CancellationToken cancellationToken) =>
        await database.ExecuteAsync(sql, parameters, cancellationToken) == 1;

    private static TimeSpan RetryDelay(int attempts)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Max(0, attempts - 1)));
        return TimeSpan.FromSeconds(seconds);
    }
}
