using System.Collections.ObjectModel;
using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Shared;

namespace Quesshi.Formats.Testing;

/// <summary>Database-free transition harness for pure format tests.</summary>
public sealed class InMemorySession : ISession, IViewing
{
    private readonly IKindRegistry _kinds;
    private readonly IClock _clock;

    public InMemorySession(SessionState initialState, IKindRegistry kinds, IClock clock, string viewer)
    {
        InitialState = initialState ?? throw new ArgumentNullException(nameof(initialState));
        CurrentState = initialState;
        _kinds = kinds ?? throw new ArgumentNullException(nameof(kinds));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
    }

    public SessionState InitialState { get; }
    public SessionState CurrentState { get; private set; }
    public ChangeSet Changes { get; private set; } = ChangeSet.Empty;
    public string Viewer { get; }

    public SessionRow Session => CurrentState.Session;
    public JsonElement State
    {
        get => CurrentState.FormatState;
        set => Transition(CurrentState with { FormatState = value.Clone() }, "sessions", Session.Id, value.Clone());
    }
    public IReadOnlyList<Seat> Seats => CurrentState.Seats.Values.OrderBy(seat => seat.Player, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<SessionItem> Items => CurrentState.Items.Values.OrderBy(item => item.Metadata.Slot).Select(item => item.Metadata).ToArray();
    public DateTimeOffset Now => _clock.Now;

    public StoredAnswer? Answer(string player, int slot) => CurrentState.Answers.TryGetValue(new AnswerKey(player, slot), out var answer)
        ? new StoredAnswer(answer.Stored?.Clone(), answer.Revision, answer.Miss)
        : null;

    StoredAnswer? IViewing.Answer(string player, int slot) =>
        player == Viewer || Session.Ended ? Answer(player, slot) : null;

    bool IViewing.HasAnswered(string player, int slot) => CurrentState.Answers.ContainsKey(new AnswerKey(player, slot));

    public Graded Grade(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var item = Item(command.At.Slot);
        var seat = SeatFor(command.Player);
        var kind = _kinds.Get(item.Metadata.Kind, item.Metadata.KindVersion);
        if (kind.Gradable && seat.ReleasedSlot >= item.Metadata.Slot)
            throw new InvalidOperationException("A gradable answer cannot be recorded after its reveal was released.");

        var duplicate = CurrentState.Answers.ContainsKey(new AnswerKey(command.Player, item.Metadata.Slot));
        var stored = kind.Normalize(item.Payload, item.Resolved, command.Payload);
        var grade = kind.Grade(item.Payload, item.Resolved, stored);
        var openedAt = Session.Position == command.At
            ? Session.PositionStartedAt
            : seat.Position == command.At ? seat.PositionStartedAt : null;
        var taken = openedAt is { } opened ? command.Received - opened : TimeSpan.Zero;
        if (taken < TimeSpan.Zero) taken = TimeSpan.Zero;
        var onTime = Session.DueAt is null || command.Received <= Session.DueAt.Value;
        return new Graded(command, stored?.Clone(), !grade.IsNone, grade.Credit, taken, onTime, duplicate);
    }

    public void Record(Graded graded, int points)
    {
        ArgumentNullException.ThrowIfNull(graded);
        var key = new AnswerKey(graded.Command.Player, graded.Command.At.Slot);
        var item = Item(key.Slot);
        var kind = _kinds.Get(item.Metadata.Kind, item.Metadata.KindVersion);
        var player = SeatFor(key.Player);
        if (kind.Gradable && player.ReleasedSlot >= key.Slot)
            throw new InvalidOperationException("A gradable answer cannot be recorded after its reveal was released.");
        if (graded.Duplicate || CurrentState.Answers.ContainsKey(key))
            throw new InvalidOperationException("An answer is already recorded for this player and slot.");
        var answer = new AnswerRecord(graded.Stored?.Clone(), graded.IsGraded, graded.Credit, points, graded.Taken, graded.Command.Received, Revision: graded.Command.Revision);
        var answers = Copy(CurrentState.Answers);
        answers.Add(key, answer);
        var seats = Copy(CurrentState.Seats);
        var seat = SeatFor(graded.Command.Player) with { Score = SeatFor(graded.Command.Player).Score + points };
        seats[seat.Player] = seat;
        Transition(CurrentState with { Answers = ReadOnly(answers), Seats = ReadOnly(seats) }, "session_answers", $"{key.Player}:{key.Slot}", answer);
        Changes = Changes.Add("session_participants", seat.Player, seat);
    }

    public void Replace(Graded graded, int revision)
    {
        ArgumentNullException.ThrowIfNull(graded);
        var key = new AnswerKey(graded.Command.Player, graded.Command.At.Slot);
        if (!CurrentState.Answers.TryGetValue(key, out var old) || revision <= old.Revision)
            throw new InvalidOperationException("Only a higher revision can replace a recorded answer.");
        var replacement = new AnswerRecord(graded.Stored?.Clone(), graded.IsGraded, graded.Credit, old.Points, graded.Taken, graded.Command.Received, Revision: revision);
        var answers = Copy(CurrentState.Answers);
        answers[key] = replacement;
        Transition(CurrentState with { Answers = ReadOnly(answers) }, "session_answers", $"{key.Player}:{key.Slot}", replacement);
    }

    public void RecordMiss(string player, int slot) => RecordMiss(new AnswerKey(player, slot));

    public void RecordMisses(int slot)
    {
        foreach (var player in Seats.Where(seat => seat.Role == "player" && !seat.Eliminated).Select(seat => seat.Player).ToArray())
            if (!CurrentState.Answers.ContainsKey(new AnswerKey(player, slot))) RecordMiss(player, slot);
    }

    public void SetPosition(int phase, int slot, int step = 0)
    {
        var row = Session with { Position = new Position(phase, slot, step), PositionStartedAt = _clock.Now };
        Transition(CurrentState with { Session = row }, "sessions", row.Id, row);
    }

    public void SetSeatPosition(string player, int phase, int slot, int step = 0)
    {
        var seat = SeatFor(player) with { Position = new Position(phase, slot, step) };
        var seats = Copy(CurrentState.Seats);
        seats[player] = seat;
        Transition(CurrentState with { Seats = ReadOnly(seats) }, "session_participants", player, seat);
    }

    public void Serve(string player, int slot)
    {
        var seat = SeatFor(player) with { Position = new Position(0, slot), PositionStartedAt = _clock.Now };
        var seats = Copy(CurrentState.Seats);
        seats[player] = seat;
        Transition(CurrentState with { Seats = ReadOnly(seats) }, "session_participants", player, seat);
    }

    public void Release(int slot)
    {
        var row = Session with { };
        var seats = Copy(CurrentState.Seats);
        foreach (var (player, seat) in seats.ToArray()) seats[player] = seat with { ReleasedSlot = Math.Max(seat.ReleasedSlot ?? -1, slot) };
        Transition(CurrentState with { Session = row, Seats = ReadOnly(seats) }, "sessions", row.Id, row);
        foreach (var seat in seats.Values) Changes = Changes.Add("session_participants", seat.Player, seat);
    }

    public void ReleaseTo(string player, int slot)
    {
        var seat = SeatFor(player) with { ReleasedSlot = Math.Max(SeatFor(player).ReleasedSlot ?? -1, slot) };
        var seats = Copy(CurrentState.Seats);
        seats[player] = seat;
        Transition(CurrentState with { Seats = ReadOnly(seats) }, "session_participants", player, seat);
    }

    public void SetDeadline(DateTimeOffset at)
    {
        var ceiling = Session.StartedAt?.AddDays(90);
        var row = Session with { DueAt = ceiling is { } limit && at > limit ? limit : at };
        Transition(CurrentState with { Session = row }, "sessions", row.Id, row);
    }

    public void ClearDeadline()
    {
        var row = Session with { DueAt = Session.StartedAt?.AddDays(90) };
        Transition(CurrentState with { Session = row }, "sessions", row.Id, row);
    }

    public void Eliminate(string player, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        var seat = SeatFor(player) with { Eliminated = true, Status = SeatStatus.Eliminated };
        var seats = Copy(CurrentState.Seats);
        seats[player] = seat;
        Transition(CurrentState with { Seats = ReadOnly(seats) }, "session_participants", player, seat);
    }

    public void SetSeatResult(string player, SeatStatus status, JsonElement result)
    {
        var seat = SeatFor(player) with { Status = status, Result = result.Clone() };
        var seats = Copy(CurrentState.Seats);
        seats[player] = seat;
        Transition(CurrentState with { Seats = ReadOnly(seats) }, "session_participants", player, seat);
    }

    /// <summary>Adds a player seat without changing the already-drawn session state.</summary>
    public void AddSeat(Seat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        var seats = Copy(CurrentState.Seats);
        if (!seats.TryAdd(seat.Player, seat))
            throw new InvalidOperationException($"Player '{seat.Player}' is already seated.");
        Transition(CurrentState with { Seats = ReadOnly(seats) }, "session_participants", seat.Player, seat);
    }

    public void End(EndReason reason, IReadOnlyList<Standing> standings, JsonElement result)
    {
        ArgumentNullException.ThrowIfNull(standings);
        var players = Seats.Where(seat => seat.Role == "player").Select(seat => seat.Player).Order().ToArray();
        if (!players.SequenceEqual(standings.Select(standing => standing.Player).Order(), StringComparer.Ordinal))
            throw new InvalidOperationException("Standings must cover every player seat.");
        var seats = Copy(CurrentState.Seats);
        foreach (var standing in standings)
        {
            var seat = seats[standing.Player];
            seats[standing.Player] = seat with
            {
                Place = standing.Place,
                Outcome = standing.Outcome,
                Status = seat.Status == SeatStatus.Active ? SeatStatus.Done : seat.Status,
            };
        }
        var row = Session with { Ended = true, EndReason = reason, Result = result.Clone(), DueAt = null };
        Transition(
            CurrentState with
            {
                Session = row,
                FormatState = JsonSerializer.SerializeToElement(new { }),
                Seats = ReadOnly(seats),
            },
            "sessions",
            row.Id,
            row);
        foreach (var standing in standings)
            Changes = Changes.Add("session_participants", standing.Player, seats[standing.Player]);
    }

    public JsonElement Card(int slot) => Item(slot).Card.Clone();

    public JsonElement? Reveal(int slot)
    {
        var seat = SeatFor(Viewer);
        return seat.ReleasedSlot >= slot ? Item(slot).Reveal.Clone() : null;
    }

    public IReadOnlyList<AnswerRecord> Answers(string player) => CurrentState.Answers
        .Where(pair => pair.Key.Player == player)
        .OrderBy(pair => pair.Key.Slot)
        .Select(pair => pair.Value)
        .ToArray();

    private void RecordMiss(AnswerKey key)
    {
        if (CurrentState.Answers.ContainsKey(key)) return;
        var answer = new AnswerRecord(null, false, 0, 0, TimeSpan.Zero, _clock.Now, Miss: true);
        var answers = Copy(CurrentState.Answers);
        answers.Add(key, answer);
        Transition(CurrentState with { Answers = ReadOnly(answers) }, "session_answers", $"{key.Player}:{key.Slot}", answer);
    }

    private void Transition(SessionState next, string table, string key, object row)
    {
        CurrentState = next;
        Changes = Changes.Add(table, key, row);
    }

    private SessionItemState Item(int slot) => CurrentState.Items.TryGetValue(slot, out var item)
        ? item
        : throw new KeyNotFoundException($"Slot {slot} is not in the session.");

    private Seat SeatFor(string player) => CurrentState.Seats.TryGetValue(player, out var seat)
        ? seat
        : throw new KeyNotFoundException($"Player '{player}' is not seated in the session.");

    private static Dictionary<TKey, TValue> Copy<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values) where TKey : notnull => new(values);
    private static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values) where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(values);
}
