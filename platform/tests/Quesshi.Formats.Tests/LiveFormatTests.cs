using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Domain;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Live;
using Quesshi.Formats.Testing;
using Quesshi.Shared;

namespace Quesshi.Formats.Tests;

public sealed class LiveFormatTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Declares_the_registered_shared_live_contract_and_measured_capacity()
    {
        var format = Assert.IsType<LiveFormat>(PlatformFormats.CreateRegistry().Get("live"));

        Assert.Equal(Pacing.Shared, format.Spec.Pacing);
        Assert.True(format.Spec.Lobby);
        Assert.True(format.Spec.Scored);
        Assert.True(format.Spec.Guests);
        Assert.True(format.Spec.Rematch);
        Assert.Equal(MatchRules.NetworkGrace, format.Spec.Grace);
        Assert.Equal(TimeSpan.FromSeconds(40), format.MaximumDeadlineLateness);
        Assert.Equal(["language", "questionCount"], format.Spec.Matchmaking!.Settings);
        Assert.True(format.Spec.Matchmaking.RequireOnline);
        Assert.Equal(2, format.Spec.MinPlayers);
        Assert.Equal(LiveFormat.MeasuredMaxPlayers, format.Spec.MaxPlayers);
        Assert.Equal(2, LiveFormat.MeasuredMaxPlayers);
        Assert.True(format.Spec.Accepts(new TestKind("graded", true)));
        Assert.False(format.Spec.Accepts(new TestKind("ungraded", false)));
    }

    [Fact]
    public void Runs_one_countdown_then_a_shared_question_and_scheduled_reveal()
    {
        var clock = new TestClock(Created);
        var session = Harness(["a", "b"], 1, clock);
        var format = Start(format: new LiveFormat(), session);

        Assert.Equal(new Position(LiveFormat.CountdownPhase, 0), session.Session.Position);
        Assert.Equal(Created + TimeSpan.FromSeconds(3), session.Session.DueAt);

        AdvanceToQuestion(format, session, clock);
        var questionEnd = Created + TimeSpan.FromSeconds(23);
        Assert.Equal(new Position(LiveFormat.QuestionPhase, 0), session.Session.Position);
        Assert.Equal(questionEnd + MatchRules.NetworkGrace, session.Session.DueAt);
        Assert.Equal(questionEnd, format.Deadline(session));

        format.Apply(session, Answer("a", 0, clock.Now));
        format.Apply(session, Answer("b", 0, clock.Now));

        Assert.Equal(new Position(LiveFormat.RevealPhase, 0), session.Session.Position);
        Assert.Equal(questionEnd + TimeSpan.FromSeconds(3), session.Session.DueAt);
    }

    [Fact]
    public void A_missing_answer_closes_at_question_deadline_plus_grace_and_reveal_ends_on_schedule()
    {
        var clock = new TestClock(Created);
        var session = Harness(["a", "b"], 2, clock);
        var format = Start(format: new LiveFormat(), session);
        AdvanceToQuestion(format, session, clock);
        var questionEnd = Created + TimeSpan.FromSeconds(23);

        format.Apply(session, Answer("a", 0, clock.Now));
        clock.Set(questionEnd + MatchRules.NetworkGrace);
        format.Timeout(session);

        Assert.True(session.Answers("b").Single().Miss);
        Assert.Equal(new Position(LiveFormat.RevealPhase, 0), session.Session.Position);
        Assert.Equal(questionEnd + TimeSpan.FromSeconds(3), session.Session.DueAt);

        clock.Set(session.Session.DueAt!.Value);
        format.Timeout(session);

        Assert.Equal(new Position(LiveFormat.QuestionPhase, 1), session.Session.Position);
        Assert.Equal(questionEnd + TimeSpan.FromSeconds(3), session.Session.PositionStartedAt);
    }

    [Fact]
    public void Leaving_before_an_answer_removes_the_player_from_expected_but_leaving_after_an_answer_does_not()
    {
        var clock = new TestClock(Created);
        var before = Harness(["a", "b", "c"], 1, clock);
        var format = Start(format: new LiveFormat(), before);
        AdvanceToQuestion(format, before, clock);

        format.Apply(before, Leave("c", 0, clock.Now));
        var afterEarlyLeave = Assert.IsType<LiveView>(format.View(before));
        Assert.DoesNotContain("c", afterEarlyLeave.Expected);
        format.Apply(before, Answer("a", 0, clock.Now));
        format.Apply(before, Answer("b", 0, clock.Now));
        Assert.Empty(before.Answers("c"));

        var after = Harness(["a", "b", "c"], 1, clock);
        format = Start(new LiveFormat(), after);
        AdvanceToQuestion(format, after, clock);
        var deadline = after.Session.DueAt;
        format.Apply(after, Answer("b", 0, clock.Now));
        format.Apply(after, Leave("b", 0, clock.Now));
        var afterAnsweredLeave = Assert.IsType<LiveView>(format.View(after));

        Assert.Contains("b", afterAnsweredLeave.Expected);
        Assert.Contains("b", afterAnsweredLeave.Answered);
        Assert.Equal(deadline, after.Session.DueAt);
    }

    [Fact]
    public void Three_consecutive_misses_eliminate_a_player_but_an_answer_resets_the_streak()
    {
        var clock = new TestClock(Created);
        var eliminated = Harness(["a", "b"], 3, clock);
        var format = Start(new LiveFormat(), eliminated);
        AdvanceToQuestion(format, eliminated, clock);
        CloseWithOnly("b", format, eliminated, clock);
        NextQuestion(format, eliminated, clock);
        CloseWithOnly("b", format, eliminated, clock);
        NextQuestion(format, eliminated, clock);
        CloseWithOnly("b", format, eliminated, clock);

        Assert.Equal(SeatStatus.Eliminated, Seat(eliminated, "a").Status);
        Assert.Equal(2, SeatResult(eliminated, "a").GetProperty("exitRound").GetInt32());
        Assert.All(eliminated.Answers("a"), answer => Assert.True(answer.Miss));

        var reset = Harness(["a", "b"], 4, clock);
        format = Start(new LiveFormat(), reset);
        AdvanceToQuestion(format, reset, clock);
        CloseWithOnly("b", format, reset, clock);
        NextQuestion(format, reset, clock);
        CloseWithOnly("b", format, reset, clock);
        NextQuestion(format, reset, clock);
        format.Apply(reset, Answer("a", 2, clock.Now));
        format.Apply(reset, Answer("b", 2, clock.Now));
        NextQuestion(format, reset, clock);
        CloseWithOnly("b", format, reset, clock);

        Assert.Equal(SeatStatus.Active, Seat(reset, "a").Status);
        Assert.Equal(3, reset.Answers("a").Count(answer => answer.Miss));
    }

    [Fact]
    public void Ranks_finishers_above_abandoners_and_uses_later_exit_round_for_abandoners()
    {
        var clock = new TestClock(Created);
        var session = Harness(["a", "b", "c"], 2, clock, scores: [0, 500, 50]);
        var format = Start(new LiveFormat(), session);
        AdvanceToQuestion(format, session, clock);
        format.Apply(session, Leave("b", 0, clock.Now));
        format.Apply(session, Answer("a", 0, clock.Now, 0));
        format.Apply(session, Answer("c", 0, clock.Now, 0));
        NextQuestion(format, session, clock);
        format.Apply(session, Leave("c", 1, clock.Now));

        var standings = Result(session).Standings;
        Assert.Collection(standings.OrderBy(standing => standing.Place),
            standing => Assert.Equal(new LiveStanding("a", 0, 1, "win"), standing),
            standing => Assert.Equal(new LiveStanding("c", 0, 2, "loss"), standing),
            standing => Assert.Equal(new LiveStanding("b", 0, 3, "loss"), standing));
        Assert.Equal(500, Seat(session, "b").Score);
        Assert.Equal(SeatStatus.Left, Seat(session, "b").Status);
        Assert.Equal(0, SeatResult(session, "b").GetProperty("standingScore").GetInt32());
    }

    [Fact]
    public void Uses_competition_places_and_win_draw_loss_outcomes()
    {
        var clock = new TestClock(Created);
        var tied = Harness(["a", "b", "c"], 1, clock, scores: [200, 200, 100]);
        var format = Start(new LiveFormat(), tied);
        AdvanceToQuestion(format, tied, clock);
        format.Apply(tied, Answer("a", 0, clock.Now, 0));
        format.Apply(tied, Answer("b", 0, clock.Now, 0));
        format.Apply(tied, Answer("c", 0, clock.Now, 0));
        FinishReveal(format, tied, clock);

        Assert.Equal([1, 1, 3], tied.Seats.Select(seat => seat.Place));
        Assert.Equal(["draw", "draw", "loss"], tied.Seats.Select(seat => seat.Outcome));

        var winner = Harness(["a", "b"], 1, clock, scores: [200, 100]);
        format = Start(new LiveFormat(), winner);
        AdvanceToQuestion(format, winner, clock);
        format.Apply(winner, Answer("a", 0, clock.Now, 0));
        format.Apply(winner, Answer("b", 0, clock.Now, 0));
        FinishReveal(format, winner, clock);

        Assert.Equal(["win", "loss"], winner.Seats.Select(seat => seat.Outcome));
    }

    [Fact]
    public void Ends_when_one_player_remains_after_the_last_reveal_or_everyone_is_eliminated()
    {
        var clock = new TestClock(Created);
        var oneLeft = Harness(["a", "b"], 1, clock);
        var format = Start(new LiveFormat(), oneLeft);
        AdvanceToQuestion(format, oneLeft, clock);
        format.Apply(oneLeft, Leave("b", 0, clock.Now));
        Assert.Equal(EndReason.Completed, oneLeft.Session.EndReason);

        var lastReveal = Harness(["a", "b"], 1, clock);
        format = Start(new LiveFormat(), lastReveal);
        AdvanceToQuestion(format, lastReveal, clock);
        format.Apply(lastReveal, Answer("a", 0, clock.Now));
        format.Apply(lastReveal, Answer("b", 0, clock.Now));
        FinishReveal(format, lastReveal, clock);
        Assert.Equal(EndReason.Completed, lastReveal.Session.EndReason);

        var noContest = Harness(["a", "b"], 3, clock);
        format = Start(new LiveFormat(), noContest);
        AdvanceToQuestion(format, noContest, clock);
        CloseWithOnly(null, format, noContest, clock);
        NextQuestion(format, noContest, clock);
        CloseWithOnly(null, format, noContest, clock);
        NextQuestion(format, noContest, clock);
        CloseWithOnly(null, format, noContest, clock);
        Assert.Equal(EndReason.NoContest, noContest.Session.EndReason);
        Assert.All(noContest.Seats, seat => Assert.Equal("none", seat.Outcome));
        Assert.All(noContest.Seats, seat => Assert.False(seat.Ranked));
    }

    private static LiveFormat Start(LiveFormat format, InMemorySession session)
    {
        format.Start(session);
        return format;
    }

    private static void AdvanceToQuestion(LiveFormat format, InMemorySession session, TestClock clock)
    {
        clock.Set(session.Session.DueAt!.Value);
        format.Timeout(session);
    }

    private static void NextQuestion(LiveFormat format, InMemorySession session, TestClock clock)
    {
        clock.Set(session.Session.DueAt!.Value);
        format.Timeout(session);
    }

    private static void CloseWithOnly(string? player, LiveFormat format, InMemorySession session, TestClock clock)
    {
        if (player is not null) format.Apply(session, Answer(player, session.Session.Position!.Slot, clock.Now));
        clock.Set(session.Session.DueAt!.Value);
        format.Timeout(session);
    }

    private static void FinishReveal(LiveFormat format, InMemorySession session, TestClock clock)
    {
        clock.Set(session.Session.DueAt!.Value);
        format.Timeout(session);
    }

    private static InMemorySession Harness(IReadOnlyList<string> players, int slots, TestClock clock, IReadOnlyList<int>? scores = null)
    {
        var seats = players.Select((player, index) => new Seat(player, "player", Score: scores?[index] ?? 0));
        var items = Enumerable.Range(0, slots).Select(slot => SessionItemState.Create(
            new SessionItem(slot, "graded", 1, null, null), Json("{}"), Json("{}"), Json($"{{\"slot\":{slot}}}"), Json($"{{\"reveal\":{slot}}}")));
        var state = SessionState.Create(new SessionRow("session", Created, null, null, Format: "live"), Json("{}"), seats, items);
        return new InMemorySession(state, new KindRegistry([new TestKind("graded", true)]), clock, "a");
    }

    private static Command Answer(string player, int slot, DateTimeOffset received, double credit = 1) =>
        new(CommandType.Answer, player, new Position(LiveFormat.QuestionPhase, slot), 0, null, Json($"{{\"credit\":{credit.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"), received);
    private static Command Leave(string player, int slot, DateTimeOffset received) =>
        new(CommandType.Leave, player, new Position(LiveFormat.QuestionPhase, slot), 0, null, null, received);
    private static Seat Seat(InMemorySession session, string player) => session.Seats.Single(seat => seat.Player == player);
    private static JsonElement SeatResult(InMemorySession session, string player) => Seat(session, player).Result!.Value;
    private static LiveResult Result(InMemorySession session) => JsonSerializer.Deserialize<LiveResult>(
        session.Session.Result!.Value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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
        public Grade Grade(JsonElement payload, JsonElement resolved, JsonElement? stored) => !gradable || stored is null
            ? Quesshi.Shared.Grade.None
            : Quesshi.Shared.Grade.Of(stored.Value.GetProperty("credit").GetDouble());
    }
}
