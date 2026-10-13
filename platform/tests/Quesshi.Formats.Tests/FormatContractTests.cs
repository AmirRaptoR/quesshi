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
    public void Transitions_are_immutable_and_changes_are_ordered()
    {
        var initial = State(players: 1, slots: 1);
        var session = Harness(initial);
        session.SetSeatPosition("a", 0, 0);
        session.ReleaseTo("a", 0);

        Assert.NotSame(initial, session.CurrentState);
        Assert.Null(initial.Seats["a"].Position);
        Assert.Null(initial.Seats["a"].ReleasedSlot);
        Assert.Equal(["session_participants", "session_participants"], session.Changes.Changes.Select(change => change.Table));
    }

    [Fact]
    public void Grade_does_not_mutate_and_release_blocks_only_gradable_kinds()
    {
        var graded = Harness(State(players: 1, slots: 1, kind: "graded"));
        var original = graded.CurrentState;
        _ = graded.Grade(Answer("a", 0));
        Assert.Same(original, graded.CurrentState);
        graded.ReleaseTo("a", 0);
        Assert.Throws<InvalidOperationException>(() => graded.Grade(Answer("a", 0)));

        var ungraded = Harness(State(players: 1, slots: 1, kind: "ungraded"));
        ungraded.ReleaseTo("a", 0);
        var result = ungraded.Grade(Answer("a", 0));
        ungraded.Record(result, 0);
        Assert.Single(ungraded.CurrentState.Answers);
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
    }

    private static InMemorySession Harness(SessionState state) => new(state, new KindRegistry([new TestKind("graded", true), new TestKind("ungraded", false), new TestKind("echo", true)]), new TestClock(Started), "a");

    private static SessionState State(int players, int slots, string kind = "graded", bool hasStarted = true)
    {
        var seats = Enumerable.Range(0, players).Select(index => new Seat(((char)('a' + index)).ToString(), "player"));
        var items = Enumerable.Range(0, slots).Select(slot => SessionItemState.Create(
            new SessionItem(slot, kind, 1, null, null), Json("{}"), Json("{}"), Json($"{{\"slot\":{slot}}}"), Json("{}")));
        return SessionState.Create(new SessionRow("session", hasStarted ? Started : null, null, null), Json("{}"), seats, items);
    }

    private static Command Answer(string player, int slot) => new(CommandType.Answer, player, new Position(0, slot), 0, null, Json("{}"), Started);
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
