using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Testing;
using Quesshi.Shared;

namespace Quesshi.Formats.Tests;

public sealed class FormatContractTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registry_only_resolves_explicitly_registered_format_ids()
    {
        IFormatRegistry registry = new FormatRegistry([new EchoFormat()]);

        Assert.Equal("echo", registry.Get("echo").Id);
        Assert.False(registry.TryGet("other", out _));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("other"));
    }

    [Fact]
    public void Contract_declares_and_exercises_all_format_operations()
    {
        IFormat format = new EchoFormat();
        var rules = format.ReadRules(Json("{}"));
        var plan = format.Plan(new ContentSpec("test", Json("{}"), 2), rules);
        var state = State(players: 1, slots: 2);
        var session = Harness(state);
        format.Start(session);
        var command = Answer("a", 0);
        format.Apply(session, command);
        format.Timeout(session);
        _ = format.View(session);
        Assert.Equal(2, plan.Count);
        Assert.Equal(JsonValueKind.Object, format.UpgradeState(1, session.State).ValueKind);
        Assert.Equal(JsonValueKind.Object, format.UpgradeRules(1, rules).ValueKind);
        Assert.Equal(JsonValueKind.Object, format.UpgradeResult(1, Json("{}")).ValueKind);
    }

    [Fact]
    public void Applying_a_command_is_immutable_and_records_ordered_answer_and_standing_changes()
    {
        var initial = State(players: 1, slots: 1);
        var session = Harness(initial);
        IFormat format = new EchoFormat();
        format.Apply(session, Answer("a", 0));

        Assert.NotSame(initial, session.CurrentState);
        Assert.Empty(initial.Answers);
        Assert.Equal(0, initial.Seats["a"].Score);
        Assert.Null(initial.Seats["a"].Place);
        Assert.True(session.CurrentState.Answers.ContainsKey(new AnswerKey("a", 0)));
        Assert.Equal(100, session.CurrentState.Seats["a"].Score);
        Assert.Equal(1, session.CurrentState.Seats["a"].Place);
        Assert.Equal(
            ["session_answers", "session_participants", "sessions", "session_participants"],
            session.Changes.Changes.Select(change => change.Table));
        Assert.Equal(
            ["a:0", "a", "session", "a"],
            session.Changes.Changes.Select(change => change.Key));
        Assert.Equal(100, Assert.IsType<AnswerRecord>(session.Changes.Changes[0].Row).Points);
        Assert.Equal(100, Assert.IsType<Seat>(session.Changes.Changes[1].Row).Score);
        Assert.True(Assert.IsType<SessionRow>(session.Changes.Changes[2].Row).Ended);
        Assert.Equal(1, Assert.IsType<Seat>(session.Changes.Changes[3].Row).Place);
    }

    [Fact]
    public void Grade_does_not_mutate_and_release_blocks_only_gradable_kinds()
    {
        var graded = Harness(State(players: 1, slots: 1, kind: "graded"));
        var original = graded.CurrentState;
        var beforeRelease = graded.Grade(Answer("a", 0));
        Assert.Same(original, graded.CurrentState);
        graded.ReleaseTo("a", 0);
        Assert.Throws<InvalidOperationException>(() => graded.Grade(Answer("a", 0)));
        Assert.Throws<InvalidOperationException>(() => graded.Record(beforeRelease, 100));

        var ungraded = Harness(State(players: 1, slots: 1, kind: "ungraded"));
        var result = ungraded.Grade(Answer("a", 0));
        ungraded.ReleaseTo("a", 0);
        ungraded.Record(result, 0);
        Assert.Single(ungraded.CurrentState.Answers);
    }

    [Fact]
    public void Grade_measures_from_shared_and_personal_clock_starts()
    {
        var sharedClock = new TestClock(Started);
        var shared = Harness(State(players: 1, slots: 1), sharedClock);
        shared.SetPosition(0, 0);
        sharedClock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), shared.Grade(Answer("a", 0, sharedClock.Now)).Taken);

        var personalClock = new TestClock(Started);
        var personal = Harness(State(players: 1, slots: 1), personalClock);
        personal.Serve("a", 0);
        personalClock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), personal.Grade(Answer("a", 0, personalClock.Now)).Taken);
    }

    [Fact]
    public void Starting_shared_or_personal_play_fixes_eligibility_for_account_players_only()
    {
        var shared = Harness(State(players: 2, slots: 1));
        shared.SetPosition(0, 0);
        Assert.All(shared.Seats, seat => Assert.True(seat.Eligible));

        var guestState = State(players: 1, slots: 1);
        guestState = guestState with { Seats = SessionState.ReadOnly(new Dictionary<string, Seat>(guestState.Seats)
        {
            ["a"] = guestState.Seats["a"] with { IsGuest = true }
        }) };
        var guest = Harness(guestState);
        guest.SetPosition(0, 0);
        Assert.False(Assert.Single(guest.Seats).Eligible);

        var upgradedGuest = guest.CurrentState with { Seats = SessionState.ReadOnly(new Dictionary<string, Seat>(guest.CurrentState.Seats)
        {
            ["a"] = guest.CurrentState.Seats["a"] with { IsGuest = false }
        }) };
        var nextSharedPosition = Harness(upgradedGuest);
        nextSharedPosition.SetPosition(0, 1);
        Assert.False(Assert.Single(nextSharedPosition.Seats).Eligible);

        var personal = Harness(State(players: 1, slots: 1));
        personal.Serve("a", 0);
        Assert.True(Assert.Single(personal.Seats).Eligible);
    }

    [Fact]
    public void Record_preserves_first_answer_revision()
    {
        var session = Harness(State(players: 1, slots: 1));
        var graded = session.Grade(Answer("a", 0, revision: 3));
        session.Record(graded, 100);

        Assert.Equal(3, session.CurrentState.Answers[new AnswerKey("a", 0)].Revision);
        Assert.Throws<InvalidOperationException>(() => session.Replace(graded, 3));
    }

    [Fact]
    public void Deadline_never_exceeds_ninety_day_lifetime_and_clear_retains_it()
    {
        var session = Harness(State(players: 1, slots: 1));
        session.SetDeadline(Started.AddDays(120));
        Assert.Equal(Started.AddDays(90), session.Session.DueAt);
        session.ClearDeadline();
        Assert.Equal(Started.AddDays(90), session.Session.DueAt);

        var notStarted = Harness(State(players: 1, slots: 1, hasStarted: false));
        notStarted.SetDeadline(Started.AddDays(120));
        Assert.Equal(Started.AddDays(120), notStarted.Session.DueAt);
        notStarted.ClearDeadline();
        Assert.Null(notStarted.Session.DueAt);
    }

    [Fact]
    public void Personal_seat_results_and_answer_rows_survive_a_terminal_transition()
    {
        var session = Harness(State(players: 2, slots: 1));
        session.Serve("a", 0);
        session.Record(session.Grade(Answer("a", 0)), 100);
        session.SetSeatResult("a", SeatStatus.Done, Json("{\"answered\":1,\"runBegan\":true}"));
        session.SetSeatResult("b", SeatStatus.Left, Json("{\"answered\":0,\"runBegan\":false,\"forfeited\":true}"));
        session.End(EndReason.Forfeit, [new Standing("a", 1, "win"), new Standing("b", 2, "loss")], Json("{}"));

        Assert.True(session.Session.Ended);
        Assert.Empty(session.State.EnumerateObject());
        Assert.Equal(SeatStatus.Done, session.Seats.Single(seat => seat.Player == "a").Status);
        Assert.Equal(SeatStatus.Left, session.Seats.Single(seat => seat.Player == "b").Status);
        Assert.True(session.Seats.Single(seat => seat.Player == "b").Result!.Value.GetProperty("forfeited").GetBoolean());
        Assert.Single(session.Answers("a"));
    }

    [Fact]
    public void Eliminating_a_seat_records_its_terminal_status()
    {
        var session = Harness(State(players: 1, slots: 1));

        session.Eliminate("a", "out");

        var seat = Assert.Single(session.Seats);
        Assert.True(seat.Eliminated);
        Assert.Equal(SeatStatus.Eliminated, seat.Status);
    }

    [Fact]
    public void Ending_a_session_marks_active_seats_done_and_preserves_terminal_statuses()
    {
        var session = Harness(State(players: 3, slots: 1));
        session.Eliminate("b", "out");
        session.SetSeatResult("c", SeatStatus.Left, Json("{\"forfeited\":true}"));

        session.End(EndReason.Completed, [
            new Standing("a", 1, "win"),
            new Standing("b", 2, "loss"),
            new Standing("c", 3, "loss")], Json("{}"));

        Assert.Equal(SeatStatus.Done, session.Seats.Single(seat => seat.Player == "a").Status);
        Assert.Equal(SeatStatus.Eliminated, session.Seats.Single(seat => seat.Player == "b").Status);
        Assert.Equal(SeatStatus.Left, session.Seats.Single(seat => seat.Player == "c").Status);
    }

    [Fact]
    public void Ending_rejects_incomplete_or_duplicate_standings_before_changing_the_session()
    {
        var session = Harness(State(players: 2, slots: 1));

        Assert.Throws<ArgumentException>(() => session.End(EndReason.Completed, [new Standing("a", 1, "win")], Json("{}")));
        Assert.Throws<ArgumentException>(() => session.End(EndReason.Completed, [new Standing("a", 1, "win"), new Standing("a", 2, "loss")], Json("{}")));
        Assert.False(session.Session.Ended);
    }

    [Fact]
    public void No_contest_normalizes_missing_outcomes_and_never_ranks_a_seat()
    {
        var session = Harness(State(players: 2, slots: 1));

        session.End(EndReason.NoContest, [new Standing("a", 1), new Standing("b", 1)], Json("{}"));

        Assert.All(session.Seats, seat =>
        {
            Assert.Equal("none", seat.Outcome);
            Assert.False(seat.Ranked);
        });
    }

    [Fact]
    public void Echo_completes_a_three_player_two_slot_session()
    {
        var session = Harness(State(players: 3, slots: 2));
        IFormat format = new EchoFormat();
        format.Start(session);
        foreach (var slot in Enumerable.Range(0, 2))
            foreach (var player in new[] { "a", "b", "c" }) format.Apply(session, Answer(player, slot));

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        Assert.Empty(session.CurrentState.FormatState.EnumerateObject());
        Assert.Equal([1, 2, 3], session.Seats.Select(seat => seat.Place));
    }

    private static InMemorySession Harness(SessionState state, TestClock? clock = null) => new(state, new KindRegistry([new TestKind("graded", true), new TestKind("ungraded", false), new TestKind("echo", true)]), clock ?? new TestClock(Started), "a");

    private static SessionState State(int players, int slots, string kind = "graded", bool hasStarted = true)
    {
        var seats = Enumerable.Range(0, players).Select(index => new Seat(((char)('a' + index)).ToString(), "player"));
        var items = Enumerable.Range(0, slots).Select(slot => SessionItemState.Create(
            new SessionItem(slot, kind, 1, null, null), Json("{}"), Json("{}"), Json($"{{\"slot\":{slot}}}"), Json("{}")));
        return SessionState.Create(new SessionRow("session", hasStarted ? Started : null, null, null, CreatedAt: Started), Json("{}"), seats, items);
    }

    private static Command Answer(string player, int slot, DateTimeOffset? received = null, int revision = 0) => new(CommandType.Answer, player, new Position(0, slot), revision, null, Json("{}"), received ?? Started);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class TestKind(string id, bool gradable) : IKindRuntime
    {
        public string Id => id;
        public int Version => 1;
        public string Family => "test";
        public bool Gradable => gradable;
        public JsonElement Upgrade(int from, JsonElement payload) => payload;
        public JsonElement UpgradeSnapshot(int from, JsonElement snapshot) => snapshot;
        public IReadOnlyList<Problem> Validate(JsonElement payload, ValidateEnv env) => [];
        public JsonElement Resolve(JsonElement payload, ResolveContext context) => payload;
        public JsonElement Card(JsonElement payload, JsonElement resolved) => payload;
        public JsonElement Reveal(JsonElement payload, JsonElement resolved) => payload;
        public JsonElement? Normalize(JsonElement payload, JsonElement resolved, JsonElement? answer) => answer;
        public Grade Grade(JsonElement payload, JsonElement resolved, JsonElement? stored) => gradable ? Quesshi.Shared.Grade.Of(1) : Quesshi.Shared.Grade.None;
    }
}
