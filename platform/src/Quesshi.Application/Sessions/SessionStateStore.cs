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
public sealed class SessionStateStore(ITenantDb database)
{
    public Task<long> SaveAsync(Guid tenantId, Guid sessionId, long expectedVersion, SessionState state, CancellationToken cancellationToken = default) =>
        SaveAsync(tenantId, sessionId, expectedVersion, SessionState.Create(state.Session, state.FormatState, [], []), state, cancellationToken);

    /// <summary>Persists only changes made by one immutable format transition.</summary>
    public Task<long> SaveAsync(Guid tenantId, Guid sessionId, long expectedVersion, SessionState initial, SessionState current, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => SaveAsync(tx, tenantId, sessionId, expectedVersion, initial, current, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    private static async Task<long> SaveAsync(ITenantTx tx, Guid tenantId, Guid sessionId, long expectedVersion, SessionState initial, SessionState current, CancellationToken cancellationToken)
    {
        var session = current.Session;
        // This is deliberately the first session-changing statement in the transaction.
        var version = await tx.QuerySingleOrDefaultAsync<long?>("""
            update sessions set version = version + 1, status = @status, phase = @phase, slot = @slot, step = @step,
              phase_started_at = @positionStartedAt, due_at = @dueAt, state = cast(@state as jsonb),
              result = cast(@result as jsonb), end_reason = @endReason,
              ended_at = case when @status in ('finished', 'cancelled') then coalesce(ended_at, now()) else null end
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
            endReason = session.EndReason?.ToString().ToLowerInvariant(),
        }, cancellationToken);
        if (version is null) throw new StaleActivationException();

        foreach (var (key, answer) in current.Answers.Where(pair =>
                     !initial.Answers.TryGetValue(pair.Key, out var previous) || !AnswerEquals(previous, pair.Value)))
        {
            var playerId = Guid.Parse(key.Player);
            await tx.ExecuteAsync("""
                insert into session_answers (tenant_id, session_id, slot, player_id, revision, answer, credit, points, taken_ms, received_at)
                values (@tenantId, @sessionId, @slot, @playerId, @revision, cast(@answer as jsonb), @credit, @points, @takenMs, @receivedAt)
                on conflict (tenant_id, session_id, slot, player_id, seq) do update set revision = excluded.revision,
                  answer = excluded.answer, credit = excluded.credit, points = excluded.points, taken_ms = excluded.taken_ms,
                  received_at = excluded.received_at
                """, new
            {
                tenantId,
                sessionId,
                slot = key.Slot,
                playerId,
                revision = answer.Revision,
                answer = answer.Miss ? null : JsonSerializer.Serialize(answer.Stored),
                credit = answer.IsGraded ? answer.Credit : (double?)null,
                answer.Points,
                takenMs = (int)Math.Min(answer.Taken.TotalMilliseconds, int.MaxValue),
                receivedAt = answer.Received
            }, cancellationToken);
        }
        foreach (var seat in current.Seats.Values.Where(seat =>
                     !initial.Seats.TryGetValue(seat.Player, out var previous) || !SeatEquals(previous, seat)))
        {
            await tx.ExecuteAsync("""
                update session_participants set status = @status, phase = @phase, slot = @slot, step = @step,
                  slot_started_at = @positionStartedAt, revealed_slot = @releasedSlot, score = @score,
                  place = @place, outcome = @outcome, result = cast(@result as jsonb)
                where tenant_id = @tenantId and session_id = @sessionId and player_id = @playerId
                """, new
            {
                tenantId,
                sessionId,
                playerId = Guid.Parse(seat.Player),
                status = SeatStatusName(seat),
                phase = seat.Position?.Phase.ToString() ?? "idle",
                slot = seat.Position?.Slot ?? -1,
                step = seat.Position?.Step ?? 0,
                seat.PositionStartedAt,
                releasedSlot = seat.ReleasedSlot ?? -1,
                seat.Score,
                seat.Place,
                seat.Outcome,
                result = JsonSerializer.Serialize(seat.Result ?? JsonSerializer.SerializeToElement(new { }))
            }, cancellationToken);
        }
        await tx.ExecuteAsync("select pg_notify('quesshi_session', @message)", new { message = $"{tenantId}:{sessionId}:{version.Value}" }, cancellationToken);
        return version.Value;
    }

    private static string SeatStatusName(Seat seat) => seat.Status switch
    {
        SeatStatus.Done => "done",
        SeatStatus.Left => "left",
        SeatStatus.Eliminated => "eliminated",
        _ => "active",
    };

    private static bool AnswerEquals(AnswerRecord left, AnswerRecord right) =>
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
        && JsonEquals(left.Result, right.Result);

    private static bool JsonEquals(JsonElement? left, JsonElement? right) =>
        left is null || right is null ? left is null && right is null : left.Value.GetRawText() == right.Value.GetRawText();
}
