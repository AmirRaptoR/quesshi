using System.Collections.Immutable;
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
    private readonly bool _scored;
    private Seat[]? _orderedSeats;

    public SessionTransition(SessionState initial, IKindRegistry kinds, IClock clock, TimeSpan grace, bool scored = false)
    {
        Initial = initial;
        Current = initial;
        _kinds = kinds;
        _clock = clock;
        _grace = grace;
        _scored = scored;
    }

    public SessionState Initial { get; }
    public SessionState Current { get; private set; }
    public bool Changed { get; private set; }
    public SessionRow Session => Current.Session;
    public JsonElement State { get => Current.FormatState; set => Set(Current with { FormatState = value.Clone() }); }
    public IReadOnlyList<Seat> Seats => _orderedSeats ??= Current.Seats.Values.OrderBy(x => x.Player, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<SessionItem> Items => Current.Items.Values.OrderBy(x => x.Metadata.Slot).Select(x => x.Metadata).ToArray();
    public int ItemCount => Current.ItemCount;
    public DateTimeOffset Now => Microseconds(_clock.Now);
    // Stored answers are immutable documents the session keeps for its lifetime, so they are handed out without a copy.
    public StoredAnswer? Answer(string player, int slot) => Current.Answers.TryGetValue(new(player, slot), out var answer)
        ? new(answer.Stored, answer.Revision, answer.Miss) : null;
    public JsonElement Card(int slot) => Item(slot).Card.Clone();

    public Graded Grade(Command command)
    {
        var item = Item(command.At.Slot);
        var seat = Seat(command.Player);
        var kind = _kinds.Get(item.Metadata.Kind, item.Metadata.KindVersion);
        if (kind.Gradable && seat.ReleasedSlot >= item.Metadata.Slot) throw new SessionCommandException("invalid_command");
        var stored = kind.Normalize(item.Payload, item.Resolved, command.Payload);
        var grade = kind.Grade(item.Payload, item.Resolved, stored);
        var opened = Session.Position == command.At ? Session.PositionStartedAt : seat.Position == command.At ? seat.PositionStartedAt : null;
        var taken = opened is { } at ? command.Received - at : TimeSpan.Zero;
        if (taken < TimeSpan.Zero) taken = TimeSpan.Zero;
        var limit = Session.DueAt is { } deadline ? deadline - (opened ?? command.Received) : TimeSpan.MaxValue;
        if (limit >= TimeSpan.Zero && taken > limit) taken = limit;
        return new(command, stored?.Clone(), !grade.IsNone, grade.Credit, taken,
            Session.DueAt is null || (_clock.Now <= Session.DueAt + _grace && command.Received <= Session.DueAt + _grace),
            Current.Answers.ContainsKey(new(command.Player, command.At.Slot)));
    }

    public void Record(Graded graded, int points)
    {
        var key = new AnswerKey(graded.Command.Player, graded.Command.At.Slot);
        if (Current.Answers.ContainsKey(key)) throw new SessionCommandException("invalid_command");
        var seat = Seat(key.Player) with { Score = Seat(key.Player).Score + points };
        Set(Current with { Answers = With(Current.Answers, key, ToAnswer(graded, points, graded.Command.Revision)), Seats = With(Current.Seats, key.Player, seat) });
    }
    public void Replace(Graded graded, int revision)
    {
        var key = new AnswerKey(graded.Command.Player, graded.Command.At.Slot);
        if (!Current.Answers.TryGetValue(key, out var old) || revision <= old.Revision) throw new SessionCommandException("invalid_command");
        Set(Current with { Answers = With(Current.Answers, key, ToAnswer(graded, old.Points, revision)) });
    }
    public void RecordMiss(string player, int slot) => RecordMiss(new(player, slot));
    public void RecordMisses(int slot) { foreach (var player in Seats.Where(x => x.Role == "player" && !x.Eliminated).Select(x => x.Player).ToArray()) RecordMiss(player, slot); }
    public void SetPosition(int phase, int slot, int step = 0)
    {
        var beginsSharedPlay = Session.Position is null;
        var seats = Copy(Current.Seats);
        foreach (var (player, seat) in seats.Where(pair => beginsSharedPlay && pair.Value.Role == "player" && !pair.Value.IsGuest).ToArray())
            seats[player] = seat with { Eligible = true };
        Set(Current with
        {
            Session = Session with { Position = new(phase, slot, step), PositionStartedAt = Now },
            Seats = ReadOnly(seats)
        });
    }
    public void SetSeatPosition(string player, int phase, int slot, int step = 0) => SetSeat(player, Seat(player) with { Position = new(phase, slot, step), PositionStartedAt = Now });
    public void Serve(string player, int slot)
    {
        var seat = Seat(player);
        SetSeat(player, seat with { Position = new(0, slot), PositionStartedAt = Now, Eligible = seat.Eligible || seat.Role == "player" && !seat.IsGuest });
    }
    public void Release(int slot) { var seats = Copy(Current.Seats); foreach (var (id, seat) in seats.ToArray()) seats[id] = seat with { ReleasedSlot = Math.Max(seat.ReleasedSlot ?? -1, slot) }; Set(Current with { Seats = ReadOnly(seats) }); }
    public void ReleaseTo(string player, int slot) => SetSeat(player, Seat(player) with { ReleasedSlot = Math.Max(Seat(player).ReleasedSlot ?? -1, slot) });
    public void SetDeadline(DateTimeOffset at) => Set(Current with { Session = Session with { DueAt = MaximumDueAt(Microseconds(at)) } });
    public void ClearDeadline() => Set(Current with { Session = Session with { DueAt = Session.StartedAt?.AddDays(90) } });
    public void Eliminate(string player, string why) => SetSeat(player, Seat(player) with { Eliminated = true, Status = SeatStatus.Eliminated });
    public void SetSeatResult(string player, SeatStatus status, JsonElement result) => SetSeat(player, Seat(player) with { Status = status, Result = result.Clone() });
    public IReadOnlyList<AnswerRecord> Answers(string player) => Current.Answers.Where(x => x.Key.Player == player).OrderBy(x => x.Key.Slot).Select(x => x.Value).ToArray();
    public void End(EndReason reason, IReadOnlyList<Standing> standings, JsonElement result)
    {
        if (reason is not (EndReason.Completed or EndReason.Forfeit or EndReason.NoContest))
            throw new ArgumentException("Formats may only end sessions as completed, forfeit, or no contest.", nameof(reason));
        ArgumentNullException.ThrowIfNull(standings);
        var players = Current.Seats.Values.Where(seat => seat.Role == "player").Select(seat => seat.Player).ToHashSet(StringComparer.Ordinal);
        var submitted = standings.Select(standing => standing.Player).ToArray();
        if (submitted.Distinct(StringComparer.Ordinal).Count() != submitted.Length
            || submitted.Length != players.Count
            || submitted.Any(player => !players.Contains(player)))
            throw new ArgumentException("Standings must cover every player seat exactly once.", nameof(standings));
        var seats = Copy(Current.Seats);
        foreach (var standing in standings)
        {
            var seat = seats[standing.Player];
            var outcome = standing.Outcome ?? "none";
            if (outcome is not ("win" or "loss" or "draw" or "none"))
                throw new ArgumentException("Standings must use win, loss, draw, or none outcomes.", nameof(standings));
            var ranked = _scored && seat.Eligible
                && reason is (EndReason.Completed or EndReason.Forfeit)
                && outcome is ("win" or "loss" or "draw");
            seats[standing.Player] = seat with
            {
                Place = standing.Place,
                Outcome = outcome,
                Ranked = ranked,
                Status = seat.Status == SeatStatus.Active ? SeatStatus.Done : seat.Status
            };
        }
        foreach (var (player, seat) in seats.ToArray())
            if (seat.Role != "player")
                seats[player] = seat with
                {
                    Ranked = false,
                    Status = seat.Status == SeatStatus.Active ? SeatStatus.Done : seat.Status
                };
        Set(Current with { Session = Session with { Ended = true, EndReason = reason, Result = result.Clone(), DueAt = null, RetryAt = null, FaultCount = 0, Status = "finished" }, Seats = ReadOnly(seats), FormatState = JsonSerializer.SerializeToElement(new { }) });
    }

    // PostgreSQL keeps microseconds. A state kept after a write must hold the value the database holds, because a timeout is delivered by its stored due time.
    private static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, value.Offset);

    private DateTimeOffset MaximumDueAt(DateTimeOffset requested) => Session.StartedAt is { } started && requested > started.AddDays(90)
        ? started.AddDays(90)
        : requested;

    private void RecordMiss(AnswerKey key) { if (Current.Answers.ContainsKey(key)) return; Set(Current with { Answers = With(Current.Answers, key, new(null, false, 0, 0, TimeSpan.Zero, Now, true)) }); }
    private void SetSeat(string player, Seat seat) => Set(Current with { Seats = With(Current.Seats, player, seat) });
    private void Set(SessionState state) { Current = state; Changed = true; _orderedSeats = null; }
    // A persistent map changes one entry in logarithmic time and keeps the others by reference; the first change converts a plain snapshot.
    private static IReadOnlyDictionary<TKey, TValue> With<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> source, TKey key, TValue value) where TKey : notnull =>
        (source as ImmutableDictionary<TKey, TValue> ?? source.ToImmutableDictionary()).SetItem(key, value);
    private SessionItemState Item(int slot) => Current.Items.TryGetValue(slot, out var item) ? item : throw new KeyNotFoundException($"Slot {slot} is not loaded.");
    private Seat Seat(string player) => Current.Seats.TryGetValue(player, out var seat) ? seat : throw new KeyNotFoundException($"Player '{player}' is not seated.");
    private static AnswerRecord ToAnswer(Graded value, int points, int revision) => new(value.Stored?.Clone(), value.IsGraded, value.Credit, points, value.Taken, value.Command.Received, Revision: revision);
    private static Dictionary<TKey, TValue> Copy<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values) where TKey : notnull => new(values);
    private static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values) where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(values);
}
