using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.Features.Matchmaking;

internal enum JoinResult { NoCandidate, Skipped, Paired, TryAgain }

/// <summary>
/// The join-waiting mode: a waiting player already owns an open session, and a joiner is seated in it by the
/// session's grain. One pairing token follows the attempt through its three steps, so a lease taken over by
/// another attempt is never released or deleted by the first.
/// </summary>
internal sealed partial class MatchmakingQueue
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EntryLives = TimeSpan.FromHours(48);

    // The session can still take another player: open, running, before its forfeit deadline, with a free seat.
    private const string Joinable = """
        s.status = 'running' and s.creation_shape = 'open' and s.due_at > now()
        and (select count(*) from session_participants sp
             where sp.session_id = s.id and sp.status = 'active' and sp.role = 'player') < s.capacity
        """;

    private async Task<EnqueueOutcome> EnqueueJoinWaitingAsync(
        ITenantTx transaction, Guid playerId, string format, LiveQueueSettings settings, CancellationToken cancellationToken)
    {
        // A player waits once: a live entry whose session can still be joined is kept as it is.
        var waiting = await transaction.QuerySingleOrDefaultAsync<bool>($"""
            select exists (
                select 1
                from matchmaking_entries e
                join sessions s on s.tenant_id = e.tenant_id and s.id = e.session_id
                where e.player_id = @playerId and e.format = @format and {Joinable})
            """, new { playerId, format }, cancellationToken);
        if (waiting) return EnqueueOutcome.Queued;

        var parameters = JsonSerializer.SerializeToElement(
            new BankParameters(NullIfEmpty(settings.Categories), NullIfEmpty(settings.Levels)),
            MatchmakingJsonContext.Default.BankParameters);
        var sessionId = Guid.NewGuid();
        var created = await sessions.CreateAsync(transaction, playerId, new SessionCreateRequest(
            sessionId,
            format,
            new SessionContent("bank", parameters, settings.QuestionCount),
            EmptyRules,
            Capacity: 2,
            SessionShape.Open), cancellationToken);
        switch (created.Status)
        {
            case SessionCreateStatus.Created:
                break;
            case SessionCreateStatus.Invalid:
                return EnqueueOutcome.InvalidSettings;
            default:
                return EnqueueOutcome.FormatUnavailable;
        }

        await transaction.ExecuteAsync("""
            insert into matchmaking_entries (tenant_id, player_id, format, match_key, settings, session_id)
            values (current_setting('app.tenant_id')::uuid, @playerId, @format, @matchKey, @settings, @sessionId)
            on conflict (tenant_id, player_id, format) do update
            set match_key = excluded.match_key,
                settings = excluded.settings,
                session_id = excluded.session_id,
                claimed_by = null,
                claimed_until = null,
                enqueued_at = now()
            """, new
        {
            playerId,
            format,
            matchKey = MatchmakingRules.MatchKey(format, settings),
            settings = new Jsonb<LiveQueueSettings>(settings, MatchmakingJsonContext.Default.LiveQueueSettings),
            sessionId,
        }, cancellationToken);
        inbox.Changed(transaction, playerId);
        return EnqueueOutcome.Queued;
    }

    /// <summary>Tries to seat one just-enqueued player in the longest-waiting compatible open session.</summary>
    internal async Task<JoinResult> AttemptJoinAsync(Guid tenantId, Guid joinerId, string format, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < AttemptsPerRequest; attempt++)
            {
                var candidate = await database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<WaitingSession>("""
                    select other.player_id as OwnerId, other.session_id as SessionId
                    from matchmaking_entries mine
                    join matchmaking_entries other on other.tenant_id = mine.tenant_id and other.format = mine.format
                                                  and other.match_key = mine.match_key and other.session_id is not null
                                                  and other.player_id <> mine.player_id
                                                  and (other.claimed_until is null or other.claimed_until <= now())
                    join players owner on owner.tenant_id = other.tenant_id and owner.id = other.player_id
                                      and owner.deleted_at is null and owner.banned_at is null
                    where mine.player_id = @joinerId and mine.format = @format and mine.session_id is not null
                      and other.enqueued_at + @entryLives > now()
                    order by other.enqueued_at, other.player_id
                    limit 1
                    """, new { joinerId, format, entryLives = EntryLives }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);
                if (candidate is null) return JoinResult.NoCandidate;
                var result = await TryJoinAsync(tenantId, format, joinerId, candidate.OwnerId, candidate.SessionId, cancellationToken);
                if (result != JoinResult.Skipped) return result;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not LockOrderViolation)
        {
        }
        return JoinResult.NoCandidate;
    }

    private async Task<JoinResult> TryJoinAsync(
        Guid tenantId, string format, Guid joinerId, Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
        // Step 1: the joiner's own entry goes and the waiting entry is leased under this attempt's token.
        var token = Guid.NewGuid();
        var leased = await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [joinerId, ownerId], cancellationToken);
            var claimed = await transaction.ExecuteAsync("""
                update matchmaking_entries
                set claimed_by = @token, claimed_until = now() + @lease
                where player_id = @ownerId and format = @format and session_id = @sessionId
                  and (claimed_until is null or claimed_until <= now())
                  and exists (select 1 from matchmaking_entries
                              where player_id = @joinerId and format = @format and session_id is not null)
                """, new { token, lease = Lease, ownerId, joinerId, format, sessionId }, cancellationToken);
            if (claimed != 1) return false;
            var removed = await transaction.ExecuteAsync("""
                delete from matchmaking_entries where player_id = @joinerId and format = @format
                """, new { joinerId, format }, cancellationToken);
            if (removed != 1) throw new PairingAbortedException();
            inbox.Changed(transaction, joinerId);
            return true;
        }, cancellationToken: cancellationToken);
        if (!leased) return JoinResult.Skipped;

        // Step 2: the session's grain seats the joiner, once for this token.
        bool? seated;
        try
        {
            seated = await seating.SeatAsync(tenantId, sessionId, joinerId, token, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not LockOrderViolation)
        {
            seated = null;
        }

        if (seated is null)
        {
            // The outcome is unknown: continue only if the joiner is seated. Otherwise the lease runs out by itself.
            try { seated = await IsSeatedAsync(tenantId, sessionId, joinerId, cancellationToken) ? true : null; }
            catch (Exception exception) when (exception is not OperationCanceledException and not LockOrderViolation) { }
            if (seated is null) return JoinResult.TryAgain;
        }

        if (seated == false)
        {
            await ReleaseAsync(tenantId, format, ownerId, sessionId, token, cancellationToken);
            return JoinResult.TryAgain;
        }

        // Step 3: the waiting entry goes, but only while it still names this session under this token.
        try
        {
            await database.InTenantAsync(tenantId, async transaction =>
            {
                await locks.PlayersAsync(transaction, [ownerId], cancellationToken);
                var deleted = await transaction.ExecuteAsync("""
                    delete from matchmaking_entries
                    where player_id = @ownerId and format = @format and session_id = @sessionId and claimed_by = @token
                    """, new { ownerId, format, sessionId, token }, cancellationToken);
                if (deleted > 0) inbox.Changed(transaction, ownerId);
                return deleted;
            }, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not LockOrderViolation)
        {
            // The joiner is seated; the sweep removes the entry once the lease runs out.
        }
        return JoinResult.Paired;
    }

    private Task<bool> IsSeatedAsync(Guid tenantId, Guid sessionId, Guid playerId, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<bool>("""
            select exists (select 1 from session_participants
                           where session_id = @sessionId and player_id = @playerId and status = 'active')
            """, new { sessionId, playerId }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);

    /// <summary>The join was refused: the waiting entry is offered again, or removed if its session cannot be joined.</summary>
    private Task ReleaseAsync(
        Guid tenantId, string format, Guid ownerId, Guid sessionId, Guid token, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [ownerId], cancellationToken);
            var parameters = new { ownerId, format, sessionId, token };
            var deleted = await transaction.ExecuteAsync($"""
                delete from matchmaking_entries e
                where e.player_id = @ownerId and e.format = @format and e.session_id = @sessionId and e.claimed_by = @token
                  and not exists (select 1 from sessions s where s.tenant_id = e.tenant_id and s.id = e.session_id and {Joinable})
                """, parameters, cancellationToken);
            if (deleted > 0) inbox.Changed(transaction, ownerId);
            return await transaction.ExecuteAsync("""
                update matchmaking_entries set claimed_by = null, claimed_until = null
                where player_id = @ownerId and format = @format and session_id = @sessionId and claimed_by = @token
                """, parameters, cancellationToken);
        }, cancellationToken: cancellationToken);

    /// <summary>Removes entries that can no longer be joined, then pairs waiters that never saw each other.</summary>
    private async Task SweepJoinWaitingAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var stale = await database.InTenantAsync(tenantId, transaction => transaction.QueryAsync<StaleEntry>($"""
            select e.player_id as PlayerId, e.format as Format
            from matchmaking_entries e
            left join sessions s on s.tenant_id = e.tenant_id and s.id = e.session_id
            where e.session_id is not null and (e.claimed_until is null or e.claimed_until <= now())
              and (e.enqueued_at + @entryLives <= now() or s.id is null or not ({Joinable}))
            limit @batch
            """, new { entryLives = EntryLives, batch = SweepBatch }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);
        foreach (var entry in stale)
            await database.InTenantAsync(tenantId, async transaction =>
            {
                await locks.PlayersAsync(transaction, [entry.PlayerId], cancellationToken);
                var deleted = await transaction.ExecuteAsync($"""
                    delete from matchmaking_entries e
                    where e.player_id = @playerId and e.format = @format and e.session_id is not null
                      and (e.claimed_until is null or e.claimed_until <= now())
                      and (e.enqueued_at + @entryLives <= now()
                           or not exists (select 1 from sessions s where s.tenant_id = e.tenant_id and s.id = e.session_id and {Joinable}))
                    """, new { playerId = entry.PlayerId, format = entry.Format, entryLives = EntryLives }, cancellationToken);
                if (deleted > 0) inbox.Changed(transaction, entry.PlayerId);
                return deleted;
            }, cancellationToken: cancellationToken);

        var waiting = await database.InTenantAsync(tenantId, transaction => transaction.QueryAsync<Waiting>("""
            select player_id as PlayerId, format as Format, match_key as MatchKey
            from matchmaking_entries
            where session_id is not null and (claimed_until is null or claimed_until <= now())
            order by format, match_key, enqueued_at, player_id
            limit @batch
            """, new { batch = SweepBatch }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);
        foreach (var group in waiting.GroupBy(entry => (entry.Format, entry.MatchKey)))
        {
            var queue = group.ToArray();
            for (var index = 0; index + 1 < queue.Length;)
            {
                var result = await PairWaitingAsync(tenantId, group.Key.Format, queue[index + 1].PlayerId, queue[index].PlayerId, cancellationToken);
                index += result == JoinResult.Paired ? 2 : 1;
            }
        }
    }

    // The earlier waiter's session is the one joined; one failing pair never blocks the others.
    private async Task<JoinResult> PairWaitingAsync(
        Guid tenantId, string format, Guid joinerId, Guid ownerId, CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = await database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<Guid?>("""
                select session_id from matchmaking_entries where player_id = @ownerId and format = @format
                """, new { ownerId, format }, cancellationToken), readOnly: true, cancellationToken: cancellationToken);
            return sessionId is { } id
                ? await TryJoinAsync(tenantId, format, joinerId, ownerId, id, cancellationToken)
                : JoinResult.Skipped;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not LockOrderViolation)
        {
            return JoinResult.Skipped;
        }
    }

    private sealed record WaitingSession(Guid OwnerId, Guid SessionId);

    private sealed record StaleEntry(Guid PlayerId, string Format);
}
