using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Domain;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.AsyncDuel;
using Quesshi.Formats.Testing;
using Quesshi.Shared;

namespace Quesshi.Formats.Tests;

public sealed class AsyncDuelFormatTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Declares_the_personal_async_duel_contract_and_draws_supported_fixed_counts()
    {
        var format = new AsyncDuelFormat();

        Assert.Equal("async_duel", format.Id);
        Assert.Equal(Pacing.Personal, format.Spec.Pacing);
        Assert.Equal(2, format.Spec.MinPlayers);
        Assert.Equal(MatchRules.MaxParticipants, format.Spec.MaxPlayers);
        Assert.True(format.Spec.Lobby);
        Assert.True(format.Spec.Scored);
        Assert.True(format.Spec.Guests);
        Assert.False(format.Spec.Rematch);
        Assert.Equal(MatchRules.NetworkGrace, format.Spec.Grace);
        Assert.Equal(["language"], format.Spec.Matchmaking!.Settings);
        Assert.False(format.Spec.Matchmaking.RequireOnline);
        Assert.True(format.Spec.Accepts(new TestKind("graded", true)));
        Assert.False(format.Spec.Accepts(new TestKind("poll", false)));

        var plan = format.Plan(new ContentSpec("fixed", Json("{}"), 20), Json("{}"));
        Assert.Equal(20, plan.Count);
        Assert.Equal(["choice", "order", "map_pick"], plan.AcceptedKinds);
        Assert.Throws<ArgumentOutOfRangeException>(() => format.Plan(new ContentSpec("fixed", Json("{}"), 9), Json("{}")));
    }

    [Fact]
    public void A_creator_can_finish_before_a_later_player_joins_without_resetting_the_session()
    {
        var clock = new TestClock(Created.AddHours(1));
        var session = Harness(State(["a"], slots: 1, createdAt: Created), clock);
        var format = new AsyncDuelFormat();
        format.Start(session);
        var deadline = session.Session.DueAt;

        format.Apply(session, Next("a"));
        format.Apply(session, Answer("a", 0, clock.Now));
        format.Apply(session, Next("a", 0, clock.Now));

        Assert.Equal(SeatStatus.Done, Seat(session, "a").Status);
        Assert.False(session.Session.Ended);
        Assert.Equal(deadline, session.Session.DueAt);
        var drawn = session.CurrentState.Items;

        session.AddSeat(new Seat("b", "player"));

        Assert.Same(drawn, session.CurrentState.Items);
        Assert.Single(session.Answers("a"));
        Assert.Equal(deadline, session.Session.DueAt);
        format.Apply(session, Next("b"));
        Assert.Equal(new Position(AsyncDuelFormat.QuestionPhase, 0), Seat(session, "b").Position);
        Assert.Equal(clock.Now, Seat(session, "b").PositionStartedAt);
        Assert.Equal(Seat(session, "a").PositionStartedAt, Created.AddHours(1));
    }

    [Fact]
    public void View_deadline_uses_the_viewers_question_clock_not_the_forfeit_clock()
    {
        var clock = new TestClock(Created);
        var session = Harness(State(["a"], slots: 1), clock);
        var format = new AsyncDuelFormat();
        format.Start(session);
        format.Apply(session, Next("a"));

        Assert.Equal(Created + MatchRules.QuestionTime, format.Deadline(session));
    }

    [Fact]
    public void Records_one_timely_answer_then_releases_and_advances_only_on_next()
    {
        var clock = new TestClock(Created);
        var session = Harness(State(["a"], slots: 2), clock);
        var format = Started(session);

        format.Apply(session, Next("a"));
        clock.Advance(TimeSpan.FromSeconds(1));
        format.Apply(session, Answer("a", 0, clock.Now));

        Assert.Single(session.Answers("a"));
        Assert.Equal(AsyncDuelFormat.AnsweredPhase, Seat(session, "a").Position!.Phase);
        Assert.Equal(0, Seat(session, "a").ReleasedSlot);
        var score = Seat(session, "a").Score;

        format.Apply(session, Answer("a", 0, clock.Now));
        format.Apply(session, Next("a", 0));
        format.Apply(session, Next("a", 1));

        Assert.Equal(score, Seat(session, "a").Score);
        Assert.Single(session.Answers("a"));
        Assert.Equal(new Position(AsyncDuelFormat.QuestionPhase, 1), Seat(session, "a").Position);
    }

    [Fact]
    public void Grace_is_judged_from_receipt_time_and_expiry_records_one_miss_on_the_next_command()
    {
        var clock = new TestClock(Created);
        var format = new AsyncDuelFormat();
        var withinGrace = Harness(State(["a"], slots: 1), clock);
        format.Start(withinGrace);
        format.Apply(withinGrace, Next("a"));
        var lateButEligible = Created + MatchRules.QuestionTime + TimeSpan.FromSeconds(1.9);
        format.Apply(withinGrace, Answer("a", 0, lateButEligible));

        Assert.Equal(100, Seat(withinGrace, "a").Score);
        Assert.False(withinGrace.Answers("a")[0].Miss);

        var expired = Harness(State(["b"], slots: 1), clock);
        format.Start(expired);
        format.Apply(expired, Next("b"));
        format.Apply(expired, Answer("b", 0, Created + MatchRules.QuestionTime + TimeSpan.FromSeconds(2.1)));
        format.Apply(expired, Answer("b", 0, Created + MatchRules.QuestionTime + TimeSpan.FromSeconds(2.2)));

        Assert.Single(expired.Answers("b"));
        Assert.True(expired.Answers("b")[0].Miss);
        Assert.Equal(0, Seat(expired, "b").Score);
        Assert.Equal(new Position(AsyncDuelFormat.AnsweredPhase, 0), Seat(expired, "b").Position);
        Assert.Equal(0, Seat(expired, "b").ReleasedSlot);
    }

    [Fact]
    public void Uses_shared_scoring_for_correct_incorrect_grace_period_and_weighted_answers()
    {
        var clock = new TestClock(Created);
        var format = new AsyncDuelFormat();

        var correct = Harness(State(["a"], slots: 1), clock);
        format.Start(correct);
        format.Apply(correct, Next("a"));
        format.Apply(correct, Answer("a", 0, Created, 1));
        Assert.Equal(160, Seat(correct, "a").Score);

        var incorrect = Harness(State(["b"], slots: 1), clock);
        format.Start(incorrect);
        format.Apply(incorrect, Next("b"));
        format.Apply(incorrect, Answer("b", 0, Created, 0));
        Assert.Equal(0, Seat(incorrect, "b").Score);

        var grace = Harness(State(["c"], slots: 1), clock);
        format.Start(grace);
        format.Apply(grace, Next("c"));
        format.Apply(grace, Answer("c", 0, Created + MatchRules.QuestionTime + TimeSpan.FromSeconds(1.9), 1));
        Assert.Equal(100, Seat(grace, "c").Score);

        var weighted = Harness(State(["d"], slots: 1, level: 5), clock);
        format.Start(weighted);
        format.Apply(weighted, Next("d"));
        format.Apply(weighted, Answer("d", 0, Created, 1));
        Assert.Equal(256, Seat(weighted, "d").Score);
    }

    [Fact]
    public void Personal_question_expiry_is_lazy_and_terminal_or_nonplayer_commands_are_stale()
    {
        var clock = new TestClock(Created);
        var session = Harness(State(["a", "spectator"], slots: 1, roles: ["player", "spectator"]), clock);
        var format = Started(session);
        format.Apply(session, Next("a"));
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(session.Answers("a"));
        format.Apply(session, Next("a", 0, clock.Now));
        Assert.Single(session.Answers("a"));
        Assert.True(session.Answers("a")[0].Miss);

        format.Apply(session, Next("a", 0));
        Assert.Equal(SeatStatus.Done, Seat(session, "a").Status);
        var answers = session.Answers("a").Count;
        format.Apply(session, Next("a", 0));
        format.Apply(session, Next("spectator"));
        Assert.Equal(answers, session.Answers("a").Count);
        Assert.Null(Seat(session, "spectator").Position);
    }

    [Fact]
    public void Views_keep_other_runs_private_until_the_viewer_is_done_and_keep_own_card_and_reveal_scoped()
    {
        var clock = new TestClock(Created);
        var session = Harness(State(["a", "b"], slots: 1), clock, viewer: "a");
        var format = Started(session);
        format.Apply(session, Next("a"));
        var beforeAnswer = Assert.IsType<AsyncDuelView>(format.View(session));
        Assert.NotNull(beforeAnswer.You!.Card);
        Assert.Null(beforeAnswer.You.Reveal);
        Assert.Empty(beforeAnswer.Others);

        format.Apply(session, Answer("a", 0, clock.Now));
        format.Apply(session, Next("a", 0));
        var done = Assert.IsType<AsyncDuelView>(format.View(session));
        Assert.NotNull(done.You!.Card);
        Assert.NotNull(done.You.Reveal);
        Assert.Single(done.Others);
        Assert.Empty(done.Others[0].Answers);

        var viewerB = Harness(session.CurrentState, clock, viewer: "b");
        var stillRunning = Assert.IsType<AsyncDuelView>(format.View(viewerB));
        Assert.Empty(stillRunning.Others);
        format.Apply(viewerB, Next("b"));
        var bCard = Assert.IsType<AsyncDuelView>(format.View(viewerB));
        Assert.NotNull(bCard.You!.Card);
        Assert.Null(bCard.You.Reveal);
        Assert.Empty(bCard.Others);
    }

    [Fact]
    public void Completes_only_after_two_player_runs_and_uses_competition_places_and_outcomes()
    {
        var clock = new TestClock(Created);
        var session = Harness(State(["a", "b", "c"], slots: 1), clock);
        var format = Started(session);

        PlayAndFinish(format, session, "a", 1, clock.Now + MatchRules.QuestionTime);
        PlayAndFinish(format, session, "b", 1, clock.Now + MatchRules.QuestionTime);
        Assert.False(session.Session.Ended);
        PlayAndFinish(format, session, "c", .5, clock.Now + MatchRules.QuestionTime);

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        Assert.Equal([1, 1, 3], session.Seats.Select(seat => seat.Place));
        Assert.Equal(["draw", "draw", "loss"], session.Seats.Select(seat => seat.Outcome));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    public void A_run_finishes_after_every_supported_question_count(int count)
    {
        var session = Harness(State(["a"], slots: count));
        var format = Started(session);

        for (var slot = 0; slot < count; slot++)
        {
            format.Apply(session, Next("a", Math.Max(0, slot - 1)));
            format.Apply(session, Answer("a", slot, Created));
        }
        format.Apply(session, Next("a", count - 1));

        Assert.Equal(SeatStatus.Done, Seat(session, "a").Status);
        Assert.Equal(count, session.Answers("a").Count);
    }

    [Fact]
    public void A_completed_session_cannot_be_forfeited()
    {
        var session = Harness(State(["a", "b"], slots: 1));
        var format = Started(session);
        PlayAndFinish(format, session, "a", 1, Created);
        PlayAndFinish(format, session, "b", 1, Created);
        var result = session.Session.Result;

        format.Timeout(session);

        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        Assert.Equal(result, session.Session.Result);
    }

    [Fact]
    public void Forfeit_is_anchored_to_creation_and_keeps_finished_runs_while_marking_unfinished_seats_left()
    {
        var clock = new TestClock(Created.AddHours(6));
        var session = Harness(State(["a", "b"], slots: 1, createdAt: Created), clock);
        var format = Started(session);
        Assert.Equal(Created + MatchRules.ForfeitAfter, session.Session.DueAt);
        PlayAndFinish(format, session, "a", 1, clock.Now);
        var savedAnswer = session.Answers("a").Single();

        format.Timeout(session);

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Forfeit, session.Session.EndReason);
        Assert.Equal(SeatStatus.Done, Seat(session, "a").Status);
        Assert.Equal(SeatStatus.Left, Seat(session, "b").Status);
        Assert.Equal(savedAnswer, session.Answers("a").Single());
        Assert.Empty(session.Answers("b"));
        Assert.True(SeatResult(session, "b").GetProperty("forfeited").GetBoolean());
        Assert.False(SeatResult(session, "b").GetProperty("runBegan").GetBoolean());
        Assert.Equal(1, SeatResult(session, "a").GetProperty("answered").GetInt32());
        Assert.True(SeatResult(session, "a").GetProperty("runBegan").GetBoolean());
        Assert.Equal("win", Seat(session, "a").Outcome);

        var result = session.Session.Result;
        format.Apply(session, Answer("b", 0, clock.Now));
        Assert.Equal(result, session.Session.Result);
        Assert.Empty(session.Answers("b"));
    }

    [Fact]
    public void A_zero_score_forfeit_ranks_all_players_as_a_draw_not_no_contest()
    {
        var session = Harness(State(["a", "b"], slots: 1));
        var format = Started(session);
        format.Apply(session, Next("a"));
        format.Timeout(session);

        Assert.Equal(EndReason.Forfeit, session.Session.EndReason);
        Assert.Equal([1, 1], session.Seats.Select(seat => seat.Place));
        Assert.Equal(["draw", "draw"], session.Seats.Select(seat => seat.Outcome));
        Assert.Empty(session.Answers("a"));
        Assert.Empty(session.Answers("b"));
        Assert.True(SeatResult(session, "a").GetProperty("runBegan").GetBoolean());
        Assert.False(SeatResult(session, "b").GetProperty("runBegan").GetBoolean());
        Assert.All(session.Seats, seat => Assert.Equal(SeatStatus.Left, seat.Status));
    }

    [Fact]
    public void A_forfeit_does_not_count_an_expired_slot_as_answered()
    {
        var session = Harness(State(["a", "b"], slots: 1));
        var format = Started(session);
        format.Apply(session, Next("a"));
        format.Apply(session, Next("a", received: Created + MatchRules.QuestionTime + MatchRules.NetworkGrace + TimeSpan.FromMilliseconds(1)));

        format.Timeout(session);

        Assert.True(session.Answers("a").Single().Miss);
        Assert.Equal(0, SeatResult(session, "a").GetProperty("answered").GetInt32());
        Assert.True(SeatResult(session, "a").GetProperty("runBegan").GetBoolean());
    }

    [Fact]
    public void A_forfeit_ranks_an_unfinished_run_by_its_banked_score()
    {
        var session = Harness(State(["a", "b", "c"], slots: 2));
        var format = Started(session);
        format.Apply(session, Next("a"));
        format.Apply(session, Answer("a", 0, Created));
        format.Apply(session, Next("a", 0));
        format.Apply(session, Answer("a", 1, Created));
        format.Apply(session, Next("a", 1));
        format.Apply(session, Next("b"));
        format.Apply(session, Answer("b", 0, Created));

        format.Timeout(session);

        Assert.Equal([1, 2, 3], session.Seats.Select(seat => seat.Place));
        Assert.Equal(SeatStatus.Left, Seat(session, "b").Status);
        Assert.Equal(1, SeatResult(session, "b").GetProperty("answered").GetInt32());
        Assert.True(Seat(session, "a").Score > Seat(session, "b").Score);
        Assert.True(Seat(session, "b").Score > Seat(session, "c").Score);
    }

    private static AsyncDuelFormat Started(InMemorySession session)
    {
        var format = new AsyncDuelFormat();
        format.Start(session);
        return format;
    }

    private static void PlayAndFinish(AsyncDuelFormat format, InMemorySession session, string player, double credit, DateTimeOffset received)
    {
        format.Apply(session, Next(player));
        format.Apply(session, Answer(player, 0, received, credit));
        format.Apply(session, Next(player, 0));
    }

    private static InMemorySession Harness(SessionState state, TestClock clock, string viewer = "a") =>
        new(state, new KindRegistry([new TestKind("graded", true)]), clock, viewer);

    private static InMemorySession Harness(SessionState state, string viewer = "a") => Harness(state, new TestClock(Created), viewer);

    private static SessionState State(IReadOnlyList<string> players, int slots, DateTimeOffset? createdAt = null, IReadOnlyList<string>? roles = null, int? level = null)
    {
        var seats = players.Select((player, index) => new Seat(player, roles?[index] ?? "player"));
        var items = Enumerable.Range(0, slots).Select(slot => SessionItemState.Create(
            new SessionItem(slot, "graded", 1, level, null), Json("{}"), Json("{}"), Json($"{{\"slot\":{slot}}}"), Json($"{{\"reveal\":{slot}}}")));
        return SessionState.Create(new SessionRow("session", Created, null, null, CreatedAt: createdAt ?? Created), Json("{}"), seats, items);
    }

    private static Command Next(string player, int slot = 0, DateTimeOffset? received = null) => new(CommandType.Next, player, new Position(AsyncDuelFormat.AnsweredPhase, slot), 0, null, null, received ?? Created);
    private static Command Answer(string player, int slot, DateTimeOffset received, double credit = 1) =>
        new(CommandType.Answer, player, new Position(AsyncDuelFormat.QuestionPhase, slot), 0, null, Json($"{{\"credit\":{credit.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"), received);
    private static Seat Seat(InMemorySession session, string player) => session.Seats.Single(seat => seat.Player == player);
    private static JsonElement SeatResult(InMemorySession session, string player) => Seat(session, player).Result!.Value;
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
