using System.Data;
using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Sessions;

/// <summary>Loads the durable command snapshot from one repeatable-read tenant transaction.</summary>
public interface ISessionSnapshotLoader
{
    Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default);
}

public sealed class SessionSnapshotLoader(ITenantDb database) : ISessionSnapshotLoader
{
    public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, transaction => LoadAsync(transaction, sessionId, cancellationToken), IsolationLevel.RepeatableRead, cancellationToken, readOnly: true);

    public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var requested = slots.Where(slot => slot >= 0).Distinct().ToArray();
        return requested.Length == 0
            ? Task.FromResult<IReadOnlyList<SessionItemState>>([])
            : database.InTenantAsync(tenantId, transaction => LoadItemsAsync(transaction, sessionId, requested, cancellationToken), IsolationLevel.RepeatableRead, cancellationToken, readOnly: true);
    }

    private static async Task<SessionState?> LoadAsync(ITenantTx transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        var row = await transaction.QuerySingleOrDefaultAsync<SessionData>("""
            select id as Id, format as Format, status as Status, phase as Phase, slot as Slot, step as Step,
                   started_at as StartedAt, due_at as DueAt, retry_at as RetryAt, version as Version,
                   state::text as State, result::text as Result, end_reason as EndReason,
                   phase_started_at as PositionStartedAt, created_at as CreatedAt,
                   (select count(*) from session_items where session_id = sessions.id) as ItemCount
            from sessions where id = @sessionId
            """, new { sessionId }, cancellationToken);
        if (row is null) return null;

        var seats = await transaction.QueryAsync<SeatData>("""
            select player_id::text as Player, role as Role, status as Status, phase as Phase, slot as Slot, step as Step,
                   score as Score, revealed_slot as ReleasedSlot, slot_started_at as PositionStartedAt,
                   place as Place, outcome as Outcome, result::text as Result
            from session_participants where session_id = @sessionId order by player_id
            """, new { sessionId }, cancellationToken);
        var items = await transaction.QueryAsync<ItemData>("""
            select slot as Slot, kind as Kind, kind_version as KindVersion, level as Level,
                   category_id::text as Category, payload::text as Payload, resolved::text as Resolved,
                   card::text as Card, reveal::text as Reveal
            from session_items
            where session_id = @sessionId
              and (@loadAllItems or slot = @sessionSlot or slot in (
                    select slot from session_participants where session_id = @sessionId and slot >= 0))
            order by slot
            """, new { sessionId, sessionSlot = row.Slot, loadAllItems = row.Status is "finished" or "cancelled" }, cancellationToken);
        var answers = await transaction.QueryAsync<AnswerData>("""
            select player_id::text as Player, slot as Slot, revision as Revision, answer::text as Answer,
                   credit as Credit, points as Points, taken_ms as TakenMs, received_at as ReceivedAt
            from session_answers
            where session_id = @sessionId
              and slot in (select slot from session_items where session_id = @sessionId
                           and (@loadAllItems or slot = @sessionSlot or slot in (
                                select slot from session_participants where session_id = @sessionId and slot >= 0)))
            """, new { sessionId, sessionSlot = row.Slot, loadAllItems = row.Status is "finished" or "cancelled" }, cancellationToken);

        var session = new SessionRow(row.Id.ToString(), row.StartedAt, row.DueAt, Position(row.Phase, row.Slot, row.Step),
            row.Status is "finished" or "cancelled", ParseEndReason(row.EndReason), Json(row.Result), row.PositionStartedAt,
            row.CreatedAt, row.Format, row.Version, row.RetryAt, row.Status);
        var state = SessionState.Create(session, Json(row.State), seats.Select(ToSeat), items.Select(ToItem), row.ItemCount);
        var mappedAnswers = answers.ToDictionary(
            answer => new AnswerKey(answer.Player, answer.Slot),
            answer => new AnswerRecord(JsonOrNull(answer.Answer), answer.Credit is not null, answer.Credit ?? 0, answer.Points,
                TimeSpan.FromMilliseconds(answer.TakenMs ?? 0), answer.ReceivedAt, answer.Answer is null, answer.Revision,
                AnswerHash(answer.Answer)));
        return state with { Answers = SessionState.ReadOnly(mappedAnswers) };
    }

    private static async Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(ITenantTx transaction, Guid sessionId, int[] slots, CancellationToken cancellationToken)
    {
        var items = await transaction.QueryAsync<ItemData>("""
            select slot as Slot, kind as Kind, kind_version as KindVersion, level as Level,
                   category_id::text as Category, payload::text as Payload, resolved::text as Resolved,
                   card::text as Card, reveal::text as Reveal
            from session_items where session_id = @sessionId and slot = any(@slots) order by slot
            """, new { sessionId, slots }, cancellationToken);
        return items.Select(ToItem).ToArray();
    }

    private static Position? Position(string? phase, int slot, int step) => phase is null ? null : new Position(PhaseNumber(phase), slot, step);
    // The current format contract uses a compact numeric phase. These persisted lifecycle phases have no format meaning.
    private static int PhaseNumber(string phase) => int.TryParse(phase, out var number) ? number : phase.ToLowerInvariant() switch
    {
        "lobby" or "idle" => -1,
        _ => throw new InvalidOperationException($"The persisted phase '{phase}' is not a format phase."),
    };
    private static EndReason? ParseEndReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "completed" or "complete" => EndReason.Completed,
        "forfeit" => EndReason.Forfeit,
        "no_contest" => EndReason.NoContest,
        _ => null,
    };
    private static Seat ToSeat(SeatData value) => new(value.Player, value.Role, Position(value.Phase, value.Slot, value.Step), value.Score,
        value.Status == "eliminated", value.ReleasedSlot, value.PositionStartedAt, value.Place, value.Outcome,
        ParseSeatStatus(value.Status), JsonOrNull(value.Result));
    private static SeatStatus ParseSeatStatus(string status) => status.ToLowerInvariant() switch
    {
        "done" => SeatStatus.Done,
        "left" => SeatStatus.Left,
        "eliminated" => SeatStatus.Eliminated,
        _ => SeatStatus.Active,
    };
    private static SessionItemState ToItem(ItemData value) => SessionItemState.Create(
        new SessionItem(value.Slot, value.Kind, value.KindVersion, value.Level, value.Category), Json(value.Payload), Json(value.Resolved), Json(value.Card), Json(value.Reveal));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement? JsonOrNull(string? value) => value is null ? null : Json(value);
    private static string? AnswerHash(string? value) => value is null
        ? null
        : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class SessionData
    {
        public Guid Id { get; init; }
        public string Format { get; init; } = "";
        public string Status { get; init; } = "";
        public string Phase { get; init; } = "";
        public int Slot { get; init; }
        public int Step { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? DueAt { get; init; }
        public DateTimeOffset? RetryAt { get; init; }
        public long Version { get; init; }
        public string State { get; init; } = "{}";
        public string Result { get; init; } = "{}";
        public string? EndReason { get; init; }
        public DateTimeOffset? PositionStartedAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public int ItemCount { get; init; }
    }
    private sealed class SeatData
    {
        public string Player { get; init; } = ""; public string Role { get; init; } = ""; public string Status { get; init; } = "";
        public string Phase { get; init; } = ""; public int Slot { get; init; } = -1; public int Step { get; init; }
        public int Score { get; init; }
        public int ReleasedSlot { get; init; }
        public DateTimeOffset? PositionStartedAt { get; init; }
        public int? Place { get; init; }
        public string? Outcome { get; init; }
        public string? Result { get; init; }
    }
    private sealed class ItemData
    {
        public int Slot { get; init; }
        public string Kind { get; init; } = ""; public int KindVersion { get; init; }
        public int? Level { get; init; }
        public string? Category { get; init; }
        public string Payload { get; init; } = "{}";
        public string Resolved { get; init; } = "{}"; public string Card { get; init; } = "{}"; public string Reveal { get; init; } = "{}";
    }
    private sealed class AnswerData
    {
        public string Player { get; init; } = ""; public int Slot { get; init; }
        public int Revision { get; init; }
        public string? Answer { get; init; }
        public double? Credit { get; init; }
        public int Points { get; init; }
        public int? TakenMs { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
    }
}
