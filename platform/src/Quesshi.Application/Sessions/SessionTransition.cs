using System.Collections.ObjectModel;
using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

/// <summary>Mutable format-facing façade over an immutable session snapshot.</summary>
internal sealed class SessionTransition : ISession
{
    private readonly IKindRegistry _kinds;
    private readonly IClock _clock;
    private readonly TimeSpan _grace;

    public SessionTransition(SessionState initial, IKindRegistry kinds, IClock clock, TimeSpan grace)
    {
        Initial = initial;
        Current = initial;
        _kinds = kinds;
        _clock = clock;
        _grace = grace;
    }

    public SessionState Initial { get; }
    public SessionState Current { get; private set; }
    public bool Changed { get; private set; }
    public SessionRow Session => Current.Session;
    public JsonElement State { get => Current.FormatState; set => Set(Current with { FormatState = value.Clone() }); }
    public IReadOnlyList<Seat> Seats => Current.Seats.Values.OrderBy(x => x.Player, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<SessionItem> Items => Current.Items.Values.OrderBy(x => x.Metadata.Slot).Select(x => x.Metadata).ToArray();
    public DateTimeOffset Now => _clock.Now;
    public StoredAnswer? Answer(string player, int slot) => Current.Answers.TryGetValue(new(player, slot), out var answer)
        ? new(answer.Stored?.Clone(), answer.Revision, answer.Miss) : null;
    public JsonElement Card(int slot) => Item(slot).Card.Clone();

    public Graded Grade(Command command)
    {
        var item = Item(command.At.Slot);
        var seat = Seat(command.Player);
        var kind = _kinds.Get(item.Metadata.Kind, item.Metadata.KindVersion);
        if (kind.Gradable && seat.ReleasedSlot >= item.Metadata.Slot) throw new InvalidOperationException("Answers cannot be recorded after reveal.");
        var stored = kind.Normalize(item.Payload, item.Resolved, command.Payload);
        var grade = kind.Grade(item.Payload, item.Resolved, stored);
        var opened = Session.Position == command.At ? Session.PositionStartedAt : seat.Position == command.At ? seat.PositionStartedAt : null;
        var taken = opened is { } at ? command.Received - at : TimeSpan.Zero;
        if (taken < TimeSpan.Zero) taken = TimeSpan.Zero;
        var limit = Session.DueAt is { } deadline ? deadline - (opened ?? command.Received) : TimeSpan.MaxValue;
        if (limit >= TimeSpan.Zero && taken > limit) taken = limit;
        return new(command, stored?.Clone(), !grade.IsNone, grade.Credit, taken,
            Session.DueAt is null || command.Received <= Session.DueAt + _grace,
            Current.Answers.ContainsKey(new(command.Player, command.At.Slot)));
    }

    public void Record(Graded graded, int points)
    {
        var key = new AnswerKey(graded.Command.Player, graded.Command.At.Slot);
        if (Current.Answers.ContainsKey(key)) throw new InvalidOperationException("An answer already exists.");
        var answers = Copy(Current.Answers); answers[key] = ToAnswer(graded, points, graded.Command.Revision);
        var seats = Copy(Current.Seats); var seat = Seat(key.Player) with { Score = Seat(key.Player).Score + points }; seats[key.Player] = seat;
        Set(Current with { Answers = ReadOnly(answers), Seats = ReadOnly(seats) });
    }
    public void Replace(Graded graded, int revision)
    {
        var key = new AnswerKey(graded.Command.Player, graded.Command.At.Slot);
        if (!Current.Answers.TryGetValue(key, out var old) || revision <= old.Revision) throw new InvalidOperationException("Only a higher revision can replace an answer.");
        var answers = Copy(Current.Answers); answers[key] = ToAnswer(graded, old.Points, revision); Set(Current with { Answers = ReadOnly(answers) });
    }
    public void RecordMiss(string player, int slot) => RecordMiss(new(player, slot));
    public void RecordMisses(int slot) { foreach (var player in Seats.Where(x => x.Role == "player" && !x.Eliminated).Select(x => x.Player).ToArray()) RecordMiss(player, slot); }
    public void SetPosition(int phase, int slot, int step = 0) => Set(Current with { Session = Session with { Position = new(phase, slot, step), PositionStartedAt = Now } });
    public void SetSeatPosition(string player, int phase, int slot, int step = 0) => SetSeat(player, Seat(player) with { Position = new(phase, slot, step), PositionStartedAt = Now });
    public void Serve(string player, int slot) => SetSeat(player, Seat(player) with { Position = new(0, slot), PositionStartedAt = Now });
    public void Release(int slot) { var seats = Copy(Current.Seats); foreach (var (id, seat) in seats.ToArray()) seats[id] = seat with { ReleasedSlot = Math.Max(seat.ReleasedSlot ?? -1, slot) }; Set(Current with { Seats = ReadOnly(seats) }); }
    public void ReleaseTo(string player, int slot) => SetSeat(player, Seat(player) with { ReleasedSlot = Math.Max(Seat(player).ReleasedSlot ?? -1, slot) });
    public void SetDeadline(DateTimeOffset at) => Set(Current with { Session = Session with { DueAt = at } });
    public void ClearDeadline() => Set(Current with { Session = Session with { DueAt = null } });
    public void Eliminate(string player, string why) => SetSeat(player, Seat(player) with { Eliminated = true, Status = SeatStatus.Eliminated });
    public void SetSeatResult(string player, SeatStatus status, JsonElement result) => SetSeat(player, Seat(player) with { Status = status, Result = result.Clone() });
    public IReadOnlyList<AnswerRecord> Answers(string player) => Current.Answers.Where(x => x.Key.Player == player).OrderBy(x => x.Key.Slot).Select(x => x.Value).ToArray();
    public void End(EndReason reason, IReadOnlyList<Standing> standings, JsonElement result)
    {
        var seats = Copy(Current.Seats);
        foreach (var standing in standings) seats[standing.Player] = seats[standing.Player] with { Place = standing.Place, Outcome = standing.Outcome, Status = seats[standing.Player].Status == SeatStatus.Active ? SeatStatus.Done : seats[standing.Player].Status };
        Set(Current with { Session = Session with { Ended = true, EndReason = reason, Result = result.Clone(), DueAt = null, Status = "finished" }, Seats = ReadOnly(seats), FormatState = JsonSerializer.SerializeToElement(new { }) });
    }

    private void RecordMiss(AnswerKey key) { if (Current.Answers.ContainsKey(key)) return; var values = Copy(Current.Answers); values[key] = new(null, false, 0, 0, TimeSpan.Zero, Now, true); Set(Current with { Answers = ReadOnly(values) }); }
    private void SetSeat(string player, Seat seat) { var values = Copy(Current.Seats); values[player] = seat; Set(Current with { Seats = ReadOnly(values) }); }
    private void Set(SessionState state) { Current = state; Changed = true; }
    private SessionItemState Item(int slot) => Current.Items.TryGetValue(slot, out var item) ? item : throw new KeyNotFoundException($"Slot {slot} is not loaded.");
    private Seat Seat(string player) => Current.Seats.TryGetValue(player, out var seat) ? seat : throw new KeyNotFoundException($"Player '{player}' is not seated.");
    private static AnswerRecord ToAnswer(Graded value, int points, int revision) => new(value.Stored?.Clone(), value.IsGraded, value.Credit, points, value.Taken, value.Command.Received, Revision: revision);
    private static Dictionary<TKey, TValue> Copy<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values) where TKey : notnull => new(values);
    private static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values) where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(values);
}
