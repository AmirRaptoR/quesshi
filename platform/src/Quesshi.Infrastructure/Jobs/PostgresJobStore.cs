using Dapper;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Jobs;

/// <summary>System-role lease and state operations for durable jobs.</summary>
internal sealed class PostgresJobStore(ISystemDb database, ISessionEffectsNotifier notifications) : IJobStore
{
    private const string Claim = """
        with due as (
            select id
            from jobs
            where status = 'ready'
              and run_at <= now()
              and (max_attempts is null or attempts < max_attempts)
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

    private const string ParkExpired = """
        with exhausted as (
            select id
            from jobs
            where status = 'ready'
              and max_attempts is not null
              and attempts >= max_attempts
              and locked_until is not null
              and locked_until <= now()
            for update skip locked
        ), parked as (
            update jobs
            set status = 'dead',
                locked_by = null,
                locked_until = null,
                last_error = coalesce(last_error, 'Job lease expired after reaching its maximum attempts.'),
                finished_at = now()
            from exhausted
            where jobs.id = exhausted.id
            returning jobs.tenant_id, jobs.owner, jobs.kind, jobs.payload
        ),
        revised as (
            insert into session_effects (tenant_id, session_id, revision)
            select tenant_id, (payload->>'subjectId')::uuid, count(*)::bigint
            from parked
            where owner = 'core'
              and kind = 'event'
              and payload->>'eventType' = 'sessionEnded'
            group by tenant_id, (payload->>'subjectId')::uuid
            on conflict (tenant_id, session_id) do update
            set revision = session_effects.revision + excluded.revision
            returning session_id
        )
        select session_id from revised
        """;

    private const string Complete = """
        update jobs
        set status = 'done', locked_by = null, locked_until = null, finished_at = now(), last_error = null
        where id = @id and status = 'ready' and locked_by = @lockedBy
        """;

    private const string Fail = """
        with transitioned as (
            update jobs
            set status = case when max_attempts is not null and attempts >= max_attempts then 'dead' else 'ready' end,
                run_at = case when max_attempts is null or attempts < max_attempts then now() + @retryDelay else run_at end,
                locked_by = null,
                locked_until = null,
                last_error = @error,
                finished_at = case when max_attempts is not null and attempts >= max_attempts then now() else null end
            where id = @id and status = 'ready' and locked_by = @lockedBy
            returning id, tenant_id, owner, kind, payload, attempts, max_attempts
        ),
        revised as (
            insert into session_effects (tenant_id, session_id, revision)
            select tenant_id, (payload->>'subjectId')::uuid, 1
            from transitioned
            where max_attempts is not null
              and attempts >= max_attempts
              and owner = 'core'
              and kind = 'event'
              and payload->>'eventType' = 'sessionEnded'
            on conflict (tenant_id, session_id) do update
            set revision = session_effects.revision + 1
            returning session_id
        )
        select id as Id, (select session_id from revised limit 1) as SessionId from transitioned
        """;

    private const string Cancel = """
        update jobs
        set status = 'cancelled', locked_by = null, locked_until = null, finished_at = now()
        where id = @id and status = 'ready' and locked_by = @lockedBy
        """;

    public PostgresJobStore(DatabaseSources sources) : this(sources, new NullSessionEffectsNotifier()) { }

    public PostgresJobStore(DatabaseSources sources, ISessionEffectsNotifier notifications) : this(new SystemDb(sources), notifications) { }

    public async Task<IReadOnlyList<LeasedJob>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        return await database.InTransactionAsync(async transaction =>
        {
            var effectsTransaction = new SystemEffectsTransaction(transaction);
            foreach (var sessionId in await transaction.QueryAsync<Guid>(ParkExpired, cancellationToken: cancellationToken))
            {
                notifications.Changed(effectsTransaction, sessionId);
            }

            return await transaction.QueryAsync<LeasedJob>(Claim, new { batchSize, lockedBy = Guid.NewGuid(), leaseDuration }, cancellationToken);
        }, cancellationToken);
    }

    public Task<bool> CompleteAsync(long id, Guid lockedBy, CancellationToken cancellationToken = default) =>
        UpdateAsync(Complete, new { id, lockedBy }, cancellationToken);

    public Task<bool> FailAsync(long id, Guid lockedBy, int attempts, int? maxAttempts, string error, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return TransitionAsync(Fail, new { id, lockedBy, retryDelay = RetryDelay(attempts), error }, cancellationToken);
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
        TransitionAsync("""
            with rerun as (
                update jobs
                set status = 'ready', run_at = now(), locked_by = null, locked_until = null, attempts = 0, last_error = null, finished_at = null
                where id = @id and status = 'dead'
                returning id, tenant_id, owner, kind, payload
            ),
            revised as (
                insert into session_effects (tenant_id, session_id, revision)
                select tenant_id, (payload->>'subjectId')::uuid, 1
                from rerun
                where owner = 'core'
                  and kind = 'event'
                  and payload->>'eventType' = 'sessionEnded'
                on conflict (tenant_id, session_id) do update
                set revision = session_effects.revision + 1
                returning session_id
            )
            select id as Id, (select session_id from revised limit 1) as SessionId from rerun
            """, new { id }, cancellationToken);

    public Task<IReadOnlyList<TenantJob>> ListDeadForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        database.QueryAsync<TenantJob>("""
            select id as Id,
                   kind as Kind,
                   attempts as Attempts,
                   last_error as LastError,
                   finished_at as OccurredAt,
                   status as Status
            from jobs
            where tenant_id = @tenantId
              and status = 'dead'
            order by finished_at, id
            """, new { tenantId }, cancellationToken);

    public Task<TenantJob?> GetForTenantAsync(Guid tenantId, long id, CancellationToken cancellationToken = default) =>
        database.QuerySingleOrDefaultAsync<TenantJob>("""
            select id as Id,
                   kind as Kind,
                   attempts as Attempts,
                   last_error as LastError,
                   finished_at as OccurredAt,
                   status as Status
            from jobs
            where tenant_id = @tenantId
              and id = @id
            """, new { tenantId, id }, cancellationToken);

    public Task<TenantJob?> RerunDeadForTenantAsync(Guid tenantId, long id, CancellationToken cancellationToken = default) =>
        database.InTransactionAsync(async transaction =>
        {
            var rerun = await transaction.QuerySingleOrDefaultAsync<TenantJobTransition?>("""
                with rerun as (
                    update jobs
                    set status = 'ready', run_at = now(), locked_by = null, locked_until = null, attempts = 0, last_error = null, finished_at = null
                    where tenant_id = @tenantId
                      and id = @id
                      and status = 'dead'
                    returning id, tenant_id, owner, kind, payload, attempts, last_error, finished_at, status
                ),
                revised as (
                    insert into session_effects (tenant_id, session_id, revision)
                    select tenant_id, (payload->>'subjectId')::uuid, 1
                    from rerun
                    where owner = 'core'
                      and kind = 'event'
                      and payload->>'eventType' = 'sessionEnded'
                    on conflict (tenant_id, session_id) do update
                    set revision = session_effects.revision + 1
                    returning session_id
                )
                select id as Id,
                       kind as Kind,
                       attempts as Attempts,
                       last_error as LastError,
                       finished_at as OccurredAt,
                       status as Status,
                       (select session_id from revised limit 1) as SessionId
                from rerun
                """, new { tenantId, id }, cancellationToken);
            if (rerun is null) return null;
            if (rerun.SessionId is { } sessionId)
            {
                notifications.Changed(new SystemEffectsTransaction(transaction), sessionId);
            }

            return new TenantJob(rerun.Id, rerun.Kind, rerun.Attempts, rerun.LastError, rerun.OccurredAt, rerun.Status);
        }, cancellationToken);

    private async Task<bool> UpdateAsync(string sql, object parameters, CancellationToken cancellationToken) =>
        await database.ExecuteAsync(sql, parameters, cancellationToken) == 1;

    private async Task<bool> TransitionAsync(string sql, object parameters, CancellationToken cancellationToken) =>
        await database.InTransactionAsync(async transaction =>
        {
            var transition = await transaction.QuerySingleOrDefaultAsync<EffectTransition?>(sql, parameters, cancellationToken);
            if (transition is null) return false;
            if (transition.SessionId is { } sessionId)
            {
                notifications.Changed(new SystemEffectsTransaction(transaction), sessionId);
            }

            return true;
        }, cancellationToken);

    private sealed record EffectTransition(long Id, Guid? SessionId);

    private sealed record TenantJobTransition(
        long Id,
        string Kind,
        int Attempts,
        string? LastError,
        DateTimeOffset? OccurredAt,
        string Status,
        Guid? SessionId);

    /// <summary>Adapts the worker's transaction for the transaction-bound notification seam.</summary>
    private sealed class SystemEffectsTransaction(ISystemTx inner) : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            inner.QueryAsync<T>(sql, parameters, cancellationToken);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(sql, parameters, cancellationToken);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
    }

    private static TimeSpan RetryDelay(int attempts)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Max(0, attempts - 1)));
        return TimeSpan.FromSeconds(seconds);
    }
}
