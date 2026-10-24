using System.Data;
using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Sessions;

public sealed class StaleActivationException : Exception
{
    public StaleActivationException() : base("The session was advanced by another activation.") { }
}

/// <summary>Persists a completed format transition behind the session version fence.</summary>
public sealed class SessionStateStore(ITenantDb database, IEvents? events = null)
{
    public Task<long> SaveAsync(Guid tenantId, Guid sessionId, long expectedVersion, SessionState state, CancellationToken cancellationToken = default) =>
        SaveAsync(tenantId, sessionId, expectedVersion, SessionState.Create(state.Session, state.FormatState, [], []), state, cancellationToken);

    /// <summary>Persists only changes made by one immutable format transition.</summary>
    public Task<long> SaveAsync(Guid tenantId, Guid sessionId, long expectedVersion, SessionState initial, SessionState current, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => SaveAsync(tx, tenantId, sessionId, expectedVersion, initial, current, events, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    private static async Task<long> SaveAsync(ITenantTx tx, Guid tenantId, Guid sessionId, long expectedVersion, SessionState initial, SessionState current, IEvents? events, CancellationToken cancellationToken)
    {
        var session = current.Session;
        // This is deliberately the first session-changing statement in the transaction.
        var version = await tx.QuerySingleOrDefaultAsync<long?>("""
            update sessions set version = version + 1, status = @status, phase = @phase, slot = @slot, step = @step,
              phase_started_at = @positionStartedAt, due_at = @dueAt, retry_at = null, fault_count = 0, state = cast(@state as jsonb),
              result = cast(@result as jsonb), end_reason = @endReason,
              ended_at = case when @status in ('finished', 'cancelled') then coalesce(ended_at, now()) else null end,
              ended_tz = case when @status in ('finished', 'cancelled') then coalesce(ended_tz, (select timezone from tenants where id = @tenantId)) else null end
            where tenant_id = @tenantId and id = @sessionId and version = @expectedVersion
            returning version
            """, new
        {
            tenantId,
            sessionId,
            expectedVersion,
            status = session.Status,
            phase = session.Position?.Phase.ToString() ?? "lobby",
            slot = session.Position?.Slot ?? -1,
            step = session.Position?.Step ?? 0,
            session.PositionStartedAt,
            session.DueAt,
            state = JsonSerializer.Serialize(current.FormatState),
            result = JsonSerializer.Serialize(session.Result ?? JsonSerializer.SerializeToElement(new { })),
            endReason = FormatEndReason(session.EndReason),
        }, cancellationToken);
        if (version is null) throw new StaleActivationException();

        var changedAnswers = current.Answers.Where(pair =>
                !initial.Answers.TryGetValue(pair.Key, out var previous) || !AnswerEquals(previous, pair.Value))
            .Select(pair => new AnswerRow(
                pair.Key.Slot,
                Guid.Parse(pair.Key.Player),
                pair.Value.Revision,
                pair.Value.Miss ? null : JsonSerializer.Serialize(pair.Value.Stored),
                pair.Value.IsGraded ? pair.Value.Credit : null,
                pair.Value.Points,
                (int)Math.Min(pair.Value.Taken.TotalMilliseconds, int.MaxValue),
                pair.Value.Received))
            .ToArray();
        // One statement per table however many answers the batch holds (architecture 7.4.1).
        if (changedAnswers.Length > 0)
        {
            await tx.ExecuteAsync("""
                insert into session_answers (tenant_id, session_id, slot, player_id, revision, answer, credit, points, taken_ms, received_at)
                select @tenantId, @sessionId, r.slot, r.player_id, r.revision, cast(r.answer as jsonb), r.credit, r.points, r.taken_ms, r.received_at
                from jsonb_to_recordset(cast(@rows as jsonb)) as r(slot int, player_id uuid, revision int, answer text, credit real, points int, taken_ms int, received_at timestamptz)
                on conflict (tenant_id, session_id, slot, player_id, seq) do update set revision = excluded.revision,
                  answer = excluded.answer, credit = excluded.credit, points = excluded.points, taken_ms = excluded.taken_ms,
                  received_at = excluded.received_at
                """, new { tenantId, sessionId, rows = JsonSerializer.Serialize(changedAnswers, RowJson) }, cancellationToken);
        }
        await WriteSeatsAsync(tx, tenantId, sessionId, initial, current, cancellationToken);
        if (!initial.Session.Ended && session.Ended && events is not null)
            await events.EmitAsync(tx, new SessionEnded(sessionId), cancellationToken);
        await NotifyAsync(tx, tenantId, sessionId, version.Value, session.Position?.Phase.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "lobby", cancellationToken);
        return version.Value;
    }

    /// <summary>Writes every seat a transition changed, exactly as a command or timeout transition does.</summary>
    internal static async Task WriteSeatsAsync(ITenantTx tx, Guid tenantId, Guid sessionId, SessionState initial, SessionState current, CancellationToken cancellationToken)
    {
        var changed = current.Seats.Values.Where(seat =>
                !initial.Seats.TryGetValue(seat.Player, out var previous) || !SeatEquals(previous, seat))
            .Select(seat => new SeatRow(
                Guid.Parse(seat.Player),
                SeatStatusName(seat),
                seat.Position?.Phase.ToString() ?? "idle",
                seat.Position?.Slot ?? -1,
                seat.Position?.Step ?? 0,
                seat.PositionStartedAt,
                seat.ReleasedSlot ?? -1,
                seat.Score,
                seat.Place,
                seat.Outcome,
                seat.Eligible,
                seat.Ranked,
                JsonSerializer.Serialize(seat.Result ?? JsonSerializer.SerializeToElement(new { }))))
            .ToArray();
        if (changed.Length == 0) return;
        await tx.ExecuteAsync("""
            update session_participants p set status = r.status, phase = r.phase, slot = r.slot, step = r.step,
              slot_started_at = r.slot_started_at, revealed_slot = r.revealed_slot, score = r.score,
              place = r.place, outcome = r.outcome, eligible = r.eligible, ranked = r.ranked, result = cast(r.result as jsonb)
            from jsonb_to_recordset(cast(@rows as jsonb)) as r(player_id uuid, status text, phase text, slot int, step int, slot_started_at timestamptz,
              revealed_slot int, score int, place int, outcome text, eligible boolean, ranked boolean, result text)
            where p.tenant_id = @tenantId and p.session_id = @sessionId and p.player_id = r.player_id
            """, new { tenantId, sessionId, rows = JsonSerializer.Serialize(changed, RowJson) }, cancellationToken);
    }

    private static readonly JsonSerializerOptions RowJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record AnswerRow(int Slot, Guid PlayerId, int Revision, string? Answer, double? Credit, int Points, int TakenMs, DateTimeOffset ReceivedAt);

    private sealed record SeatRow(Guid PlayerId, string Status, string Phase, int Slot, int Step, DateTimeOffset? SlotStartedAt,
        int RevealedSlot, int Score, int? Place, string? Outcome, bool Eligible, bool? Ranked, string Result);

    internal static Task NotifyAsync(ITenantTx tx, Guid tenantId, Guid sessionId, long version, string phase, CancellationToken cancellationToken) =>
         tx.ExecuteAsync("select pg_notify('quesshi_session', @message)", new { message = $"{tenantId}:{sessionId}:{version}:{phase}" }, cancellationToken);

    private static string? FormatEndReason(EndReason? reason) => reason switch
    {
        EndReason.Completed => "completed",
        EndReason.Forfeit => "forfeit",
        EndReason.NoContest => "no_contest",
        _ => null
    };

    /// <summary>Ends a session without deserializing or invoking its format state.</summary>
    public Task<bool> EndCoreAsync(Guid tenantId, Guid sessionId, CoreEndReason reason, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => EndCoreAsync(tx, tenantId, sessionId, reason, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    private async Task<bool> EndCoreAsync(ITenantTx tx, Guid tenantId, Guid sessionId, CoreEndReason reason, CancellationToken cancellationToken)
    {
        var version = await tx.QuerySingleOrDefaultAsync<long?>("""
            update sessions set version = version + 1, status = 'cancelled', phase = 'lobby', slot = -1, step = 0,
              phase_started_at = null, due_at = null, retry_at = null, fault_count = 0,
              state = '{}'::jsonb, result = '{}'::jsonb, end_reason = @reason, ended_at = now(),
              ended_tz = (select timezone from tenants where id = @tenantId)
            where tenant_id = @tenantId and id = @sessionId and status not in ('finished', 'cancelled')
            returning version
            """, new { tenantId, sessionId, reason = reason.ToString().ToLowerInvariant() }, cancellationToken);
        if (version is null) return false;
        await tx.ExecuteAsync("""
            update session_participants set outcome = 'none', ranked = false
            where tenant_id = @tenantId and session_id = @sessionId
            """, new { tenantId, sessionId }, cancellationToken);
        if (events is not null) await events.EmitAsync(tx, new SessionEnded(sessionId), cancellationToken);
        await tx.ExecuteAsync("select pg_notify('quesshi_session', @message)", new { message = $"{tenantId}:{sessionId}:{version.Value}:lobby" }, cancellationToken);
        return true;
    }

    /// <summary>Records a format or kind failure behind the version fence, terminally cancelling the fifth failure.</summary>
    public Task<SessionFaultResult> RecordFaultAsync(Guid tenantId, Guid sessionId, long expectedVersion, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => RecordFaultAsync(tx, tenantId, sessionId, expectedVersion, retryAt, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    private async Task<SessionFaultResult> RecordFaultAsync(ITenantTx tx, Guid tenantId, Guid sessionId, long expectedVersion, DateTimeOffset retryAt, CancellationToken cancellationToken)
    {
        var change = await tx.QuerySingleOrDefaultAsync<FaultChange>("""
            update sessions set version = version + 1,
              status = case when fault_count + 1 >= 5 then 'cancelled' else status end,
              phase = case when fault_count + 1 >= 5 then 'lobby' else phase end,
              slot = case when fault_count + 1 >= 5 then -1 else slot end,
              step = case when fault_count + 1 >= 5 then 0 else step end,
              phase_started_at = case when fault_count + 1 >= 5 then null else phase_started_at end,
              due_at = case when fault_count + 1 >= 5 then null else due_at end,
              retry_at = case when fault_count + 1 >= 5 then null else @retryAt end,
              fault_count = case when fault_count + 1 >= 5 then 0 else fault_count + 1 end,
              state = case when fault_count + 1 >= 5 then '{}'::jsonb else state end,
              result = case when fault_count + 1 >= 5 then '{}'::jsonb else result end,
              end_reason = case when fault_count + 1 >= 5 then 'fault' else end_reason end,
              ended_at = case when fault_count + 1 >= 5 then now() else ended_at end,
              ended_tz = case when fault_count + 1 >= 5 then (select timezone from tenants where id = @tenantId) else ended_tz end
            where tenant_id = @tenantId and id = @sessionId and version = @expectedVersion and status not in ('finished', 'cancelled')
            returning version as Version, status as Status, fault_count as FaultCount, retry_at as RetryAt
            """, new { tenantId, sessionId, expectedVersion, retryAt }, cancellationToken);
        if (change is null) throw new StaleActivationException();
        var ended = change.Status == "cancelled";
        if (ended)
        {
            await tx.ExecuteAsync("update session_participants set outcome = 'none', ranked = false where tenant_id = @tenantId and session_id = @sessionId", new { tenantId, sessionId }, cancellationToken);
            if (events is not null) await events.EmitAsync(tx, new SessionEnded(sessionId), cancellationToken);
        }
        await tx.ExecuteAsync("select pg_notify('quesshi_session', @message)", new { message = $"{tenantId}:{sessionId}:{change.Version}:lobby" }, cancellationToken);
        return new(ended, change.Version, change.FaultCount, change.RetryAt);
    }

    private static string SeatStatusName(Seat seat) => seat.Status switch
    {
        SeatStatus.Done => "done",
        SeatStatus.Left => "left",
        SeatStatus.Eliminated => "eliminated",
        _ => "active",
    };

    // Records are immutable and a transition copies unchanged entries by reference, so identity settles most comparisons.
    private static bool AnswerEquals(AnswerRecord left, AnswerRecord right) =>
        ReferenceEquals(left, right) ||
        left.IsGraded == right.IsGraded
        && left.Credit == right.Credit
        && left.Points == right.Points
        && left.Taken == right.Taken
        && left.Received == right.Received
        && left.Miss == right.Miss
        && left.Revision == right.Revision
        && left.Hash == right.Hash
        && JsonEquals(left.Stored, right.Stored);

    private static bool SeatEquals(Seat left, Seat right) =>
        ReferenceEquals(left, right) ||
        left.Player == right.Player
        && left.Role == right.Role
        && left.Position == right.Position
        && left.Score == right.Score
        && left.Eliminated == right.Eliminated
        && left.ReleasedSlot == right.ReleasedSlot
        && left.PositionStartedAt == right.PositionStartedAt
        && left.Place == right.Place
        && left.Outcome == right.Outcome
        && left.Status == right.Status
        && left.Eligible == right.Eligible
        && left.Ranked == right.Ranked
        && JsonEquals(left.Result, right.Result);

    private static bool JsonEquals(JsonElement? left, JsonElement? right) =>
        left is null || right is null ? left is null && right is null : left.Value.GetRawText() == right.Value.GetRawText();

    private sealed class FaultChange
    {
        public long Version { get; init; }
        public string Status { get; init; } = "";
        public short FaultCount { get; init; }
        public DateTimeOffset? RetryAt { get; init; }
    }
}

public sealed record SessionFaultResult(bool Ended, long Version, short FaultCount, DateTimeOffset? RetryAt);
