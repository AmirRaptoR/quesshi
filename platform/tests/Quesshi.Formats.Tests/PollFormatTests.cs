using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Poll;
using Quesshi.Formats.Testing;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;
using PollVotingRules = Quesshi.Formats.Poll.VotingRules;

namespace Quesshi.Formats.Tests;

public sealed class PollFormatTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Poll_is_registered_with_the_declared_unscored_lobby_contract()
    {
        IFormat format = PlatformFormats.CreateRegistry().Get("poll");

        Assert.Equal(Pacing.Shared, format.Spec.Pacing);
        Assert.Equal(2, format.Spec.MinPlayers);
        Assert.Equal(8, format.Spec.MaxPlayers);
        Assert.True(format.Spec.Lobby);
        Assert.False(format.Spec.Scored);
        Assert.True(format.Spec.Guests);
        Assert.Null(format.Spec.Matchmaking);
        Assert.False(format.Spec.Rematch);
        Assert.Equal(TimeSpan.Zero, format.Spec.Grace);
        Assert.True(format.Spec.Accepts(new PlainKind("poll_choice", "poll")));
        Assert.True(format.Spec.Accepts(new PlainKind("poll_roster", "poll")));
        Assert.False(format.Spec.Accepts(new PlainKind("choice", "quiz")));
        Assert.Equal(["poll_choice", "poll_roster"], format.Plan(new ContentSpec("test", Json("{}"), 2), Json("{}")).AcceptedKinds);
        Assert.Throws<ArgumentOutOfRangeException>(() => format.Plan(new ContentSpec("test", Json("{}"), 0), Json("{}")));
        Assert.Throws<JsonException>(() => format.ReadRules(Json("[]")));
    }

    [Fact]
    public void Start_serves_slot_zero_and_requires_a_valid_poll_roster()
    {
        var format = new PollFormat();
        var session = Harness(State(2, ChoiceItem(0)));

        format.Start(session);

        Assert.Equal(new Position(0, 0), session.Session.Position);
        Assert.Equal(Started.Add(PollVotingRules.IdleAfter), session.Session.DueAt);
        Assert.Equal(Started, session.Session.PositionStartedAt);
        Assert.Throws<InvalidOperationException>(() => format.Start(Harness(State(1, ChoiceItem(0)))));
        Assert.Throws<InvalidOperationException>(() => format.Start(Harness(State(9, ChoiceItem(0)))));
        Assert.Throws<InvalidOperationException>(() => format.Start(Harness(State(2))));
        Assert.Throws<InvalidOperationException>(() => format.Start(Harness(State(2, ChoiceItem(0, "echo")))));
    }

    [Fact]
    public void Revisions_replace_one_answer_row_without_reopening_or_recounting()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0)));
        format.Start(session);

        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}", revision: 0));
        var beforeRetry = session.CurrentState;
        format.Apply(session, Answer("a", 0, "{\"option\":\"blue\"}", revision: 1));
        Assert.Single(session.CurrentState.Answers);
        Assert.Equal(1, session.Answer("a", 0)!.Revision);
        Assert.Equal("blue", session.Answer("a", 0)!.Value!.Value.GetProperty("option").GetString());

        var beforeSameRevision = session.CurrentState;
        format.Apply(session, Answer("a", 0, "{\"option\":\"blue\"}", revision: 1));
        Assert.Same(beforeSameRevision, session.CurrentState);
        var conflict = Assert.Throws<PollRevisionConflictException>(() => format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}", revision: 1)));
        Assert.Equal("revision_conflict", conflict.Code);
        Assert.Same(beforeSameRevision, session.CurrentState);

        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}", revision: 0));
        Assert.Same(beforeSameRevision, session.CurrentState);
        Assert.Throws<ArgumentOutOfRangeException>(() => format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}", revision: -1)));
        Assert.Throws<MalformedAnswer>(() => format.Apply(session, Answer("b", 0, "{}")));
        Assert.Single(session.CurrentState.Answers);
        Assert.NotSame(beforeRetry, session.CurrentState);
    }

    [Fact]
    public void Running_view_shows_only_the_viewers_answer_and_recovers_from_answer_rows()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0)), viewer: "a");
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}", revision: 2));
        format.Apply(session, Answer("b", 0, "{\"option\":\"blue\"}"));

        var view = Assert.IsType<PollRunningView>(format.View(session));
        Assert.Equal(2, view.Revision);
        Assert.Equal("red", view.Answer!.Value.GetProperty("option").GetString());
        Assert.Equal(["a", "b"], view.AnsweredSeats);
        var rendered = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("\"Answer\":{\"option\":\"blue\"}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("PairStats", rendered, StringComparison.Ordinal);

        var restored = Harness(session.CurrentState, viewer: "a");
        var restoredView = Assert.IsType<PollRunningView>(format.View(restored));
        Assert.Equal(view.Revision, restoredView.Revision);
        Assert.True(JsonElement.DeepEquals(view.Answer!.Value, restoredView.Answer!.Value));
        Assert.Equal(session.Session.DueAt, restored.Session.DueAt);
        Assert.Equal(session.Seats, restored.Seats);
        Assert.IsType<PollUnavailableView>(format.View(Harness(session.CurrentState, viewer: "outsider")));
    }

    [Fact]
    public void Last_active_first_answer_advances_and_closed_slot_commands_do_not_change_it()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1)));
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}"));
        format.Apply(session, Answer("b", 0, "{\"option\":\"red\"}"));
        Assert.Equal(new Position(0, 0), session.Session.Position);

        format.Apply(session, Answer("c", 0, "{\"option\":\"blue\"}"));
        Assert.Equal(new Position(0, 1), session.Session.Position);
        Assert.Equal(Started.Add(PollVotingRules.IdleAfter), session.Session.DueAt);
        var afterClose = session.CurrentState;
        format.Apply(session, Answer("a", 0, "{\"option\":\"blue\"}", revision: 1));
        Assert.Same(afterClose, session.CurrentState);
    }

    [Fact]
    public void Slot_idle_expiry_leaves_unanswered_players_and_gives_the_next_slot_a_new_deadline()
    {
        var format = new PollFormat();
        var clock = new TestClock(Started);
        var session = Harness(State(5, ChoiceItem(0), ChoiceItem(1)), clock);
        format.Start(session);
        foreach (var player in new[] { "a", "b", "c" }) format.Apply(session, Answer(player, 0, "{\"option\":\"red\"}", received: clock.Now));

        clock.Advance(PollVotingRules.IdleAfter - TimeSpan.FromTicks(1));
        format.Timeout(session);
        Assert.All(session.Seats, seat => Assert.False(seat.Eliminated));
        clock.Advance(TimeSpan.FromTicks(1));
        format.Timeout(session);

        Assert.Equal(new Position(0, 1), session.Session.Position);
        Assert.Equal(clock.Now.Add(PollVotingRules.IdleAfter), session.Session.DueAt);
        Assert.True(session.Seats.Single(seat => seat.Player == "d").Eliminated);
        Assert.True(session.Seats.Single(seat => seat.Player == "e").Eliminated);
        Assert.Equal(3, session.CurrentState.Answers.Count);
        Assert.Equal("red", session.Answer("a", 0)!.Value!.Value.GetProperty("option").GetString());
    }

    [Fact]
    public void Expiry_with_fewer_than_two_active_players_ends_no_contest_without_an_open_slot_result()
    {
        var format = new PollFormat();
        var clock = new TestClock(Started);
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1)), clock);
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}"));
        clock.Advance(PollVotingRules.IdleAfter);
        format.Timeout(session);

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.NoContest, session.Session.EndReason);
        var result = Result(session);
        Assert.Empty(result.Slots);
        Assert.Null(result.PairStats);
        Assert.Null(result.AllAgreedCount);
        Assert.NotNull(session.Answer("a", 0));
        Assert.All(session.Seats.Where(seat => seat.Player != "a"), seat => Assert.True(seat.Eliminated));
        var view = Assert.IsType<PollFinishedView>(format.View(session));
        Assert.Empty(view.Slots);
    }

    [Fact]
    public void No_contest_keeps_closed_slots_but_not_the_open_slot()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1)));
        format.Start(session);
        foreach (var player in new[] { "a", "b", "c" }) format.Apply(session, Answer(player, 0, "{\"option\":\"red\"}"));
        format.Apply(session, Answer("a", 1, "{\"option\":\"blue\"}"));
        format.Apply(session, Leave("b", 1));
        format.Apply(session, Leave("c", 1));

        var result = Result(session);
        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.NoContest, session.Session.EndReason);
        Assert.Single(result.Slots);
        Assert.Equal(0, result.Slots[0].Slot);
        Assert.Null(result.PairStats);
        var view = Assert.IsType<PollFinishedView>(format.View(session));
        Assert.Single(view.Slots);
        Assert.Equal(3, view.Slots[0].Answers.Count);
        Assert.Contains(view.Seats, seat => seat.Player == "b" && !seat.Active);
        Assert.NotNull(session.Answer("a", 1));
    }

    [Fact]
    public void Completed_poll_releases_answers_counts_and_agreement_with_not_applicable_excluded()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1)));
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}"));
        format.Apply(session, Answer("b", 0, "{\"option\":\"red\"}"));
        format.Apply(session, Answer("c", 0, "{\"notApplicable\":true}"));
        foreach (var player in new[] { "a", "b", "c" }) format.Apply(session, Answer(player, 1, "{\"option\":\"blue\"}"));

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        var result = Result(session);
        Assert.Equal([2, 0, 1], result.Slots[0].Counts);
        Assert.Equal([0, 3, 0], result.Slots[1].Counts);
        Assert.Equal(2, result.AllAgreedCount);
        Assert.Equal(100, result.PairStats!.Single(pair => pair.FirstPlayer == "a" && pair.SecondPlayer == "c").AgreementPercent);
        var view = Assert.IsType<PollFinishedView>(format.View(session));
        Assert.Equal(2, view.Slots.Count);
        Assert.Equal(3, view.Slots[0].Answers.Count);
    }

    [Fact]
    public void Roster_choices_keep_frozen_order_and_count_several_and_none()
    {
        var format = new PollFormat();
        var session = Harness(State(3, RosterItem(0)));
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"several\":true}"));
        format.Apply(session, Answer("b", 0, "{\"several\":true}"));
        format.Apply(session, Answer("c", 0, "{\"none\":true}"));

        var result = Result(session);
        Assert.Equal([0, 0, 0, 2, 1, 0], result.Slots[0].Counts);
        Assert.Equal(100, result.PairStats!.Single(pair => pair.FirstPlayer == "a" && pair.SecondPlayer == "b").AgreementPercent);
    }

    [Fact]
    public void Viewing_contract_hides_other_seats_answers_until_the_session_ends()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1)), viewer: "a");
        format.Start(session);
        format.Apply(session, Answer("b", 0, "{\"option\":\"blue\"}"));

        var open = Assert.IsType<PollRunningView>(format.View(session));
        Assert.Null(open.Answer);
        Assert.Null(open.Revision);
        Assert.Equal(["b"], open.AnsweredSeats);
        var viewing = (IViewing)session;
        Assert.Null(viewing.Answer("b", 0));
        Assert.True(viewing.HasAnswered("b", 0));
        Assert.False(viewing.HasAnswered("c", 0));

        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}"));
        format.Apply(session, Answer("c", 0, "{\"option\":\"red\"}"));
        var closed = Assert.IsType<PollRunningView>(format.View(session));
        Assert.Null(closed.Answer);
        Assert.Null(viewing.Answer("b", 0));
        Assert.DoesNotContain("\"option\":\"blue\"", JsonSerializer.Serialize(closed), StringComparison.Ordinal);
        Assert.Null(closed.Revision);

        foreach (var player in new[] { "a", "b", "c" }) format.Apply(session, Answer(player, 1, "{\"option\":\"red\"}"));
        Assert.Equal("blue", viewing.Answer("b", 0)!.Value!.Value.GetProperty("option").GetString());
    }

    [Fact]
    public void Completed_view_keeps_departed_players_answers_seat_status_and_pair_stats()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1)));
        format.Start(session);
        foreach (var player in new[] { "a", "b", "c" }) format.Apply(session, Answer(player, 0, "{\"option\":\"red\"}"));
        format.Apply(session, Leave("c", 1));
        format.Apply(session, Answer("a", 1, "{\"option\":\"blue\"}"));
        format.Apply(session, Answer("b", 1, "{\"option\":\"blue\"}"));

        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        var view = Assert.IsType<PollFinishedView>(format.View(session));
        Assert.Contains(view.Seats, seat => seat.Player == "c" && !seat.Active);
        Assert.Contains(view.Slots[0].Answers, answer => answer.Player == "c" && answer.Value!.Value.GetProperty("option").GetString() == "red");
        Assert.Equal(2, view.Slots[1].Answers.Count);
        var pair = Result(session).PairStats!.Single(p => p.FirstPlayer == "a" && p.SecondPlayer == "c");
        Assert.Equal(100, pair.AgreementPercent);
    }

    [Fact]
    public void Completed_roster_view_releases_several_and_none_answers()
    {
        var format = new PollFormat();
        var session = Harness(State(3, RosterItem(0)));
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"several\":true}"));
        format.Apply(session, Answer("b", 0, "{\"several\":true}"));
        format.Apply(session, Answer("c", 0, "{\"none\":true}"));

        var view = Assert.IsType<PollFinishedView>(format.View(session));
        var answers = view.Slots[0].Answers;
        Assert.True(answers.Single(answer => answer.Player == "a").Value!.Value.GetProperty("several").GetBoolean());
        Assert.True(answers.Single(answer => answer.Player == "c").Value!.Value.GetProperty("none").GetBoolean());
    }

    [Fact]
    public void Results_leave_not_applicable_out_of_pair_comparisons_and_round_deterministically()
    {
        var format = new PollFormat();
        var session = Harness(State(3, ChoiceItem(0), ChoiceItem(1), ChoiceItem(2)));
        format.Start(session);
        for (var slot = 0; slot < 3; slot++)
        {
            format.Apply(session, Answer("a", slot, "{\"option\":\"red\"}"));
            format.Apply(session, Answer("b", slot, slot == 2 ? "{\"option\":\"blue\"}" : "{\"option\":\"red\"}"));
            format.Apply(session, Answer("c", slot, "{\"notApplicable\":true}"));
        }

        var result = Result(session);
        Assert.Equal(67, result.PairStats!.Single(pair => pair.FirstPlayer == "a" && pair.SecondPlayer == "b").AgreementPercent);
        Assert.Null(result.PairStats!.Single(pair => pair.FirstPlayer == "a" && pair.SecondPlayer == "c").AgreementPercent);
        Assert.Equal([true, true, false], result.Slots.Select(slot => slot.AllAgreed));
    }

    [Fact]
    public void A_slot_with_fewer_than_two_comparable_answers_is_not_all_agreed()
    {
        var format = new PollFormat();
        var session = Harness(State(2, ChoiceItem(0)));
        format.Start(session);
        format.Apply(session, Answer("a", 0, "{\"option\":\"red\"}"));
        format.Apply(session, Answer("b", 0, "{\"notApplicable\":true}"));

        var result = Result(session);
        Assert.False(result.Slots[0].AllAgreed);
        Assert.Null(Assert.Single(result.PairStats!).AgreementPercent);
    }

    [Fact]
    public void State_rules_and_result_versions_support_the_current_upgrade_contract()
    {
        var format = new PollFormat();
        var persisted = Json("{\"version\":1}");

        Assert.Equal(new FormatVersions(1, 1, 1), format.Versions);
        Assert.True(JsonElement.DeepEquals(persisted, format.UpgradeState(1, persisted)));
        Assert.True(JsonElement.DeepEquals(persisted, format.UpgradeRules(1, persisted)));
        Assert.True(JsonElement.DeepEquals(persisted, format.UpgradeResult(1, persisted)));
        Assert.Throws<ArgumentOutOfRangeException>(() => format.UpgradeState(0, persisted));
    }

    private static InMemorySession Harness(SessionState state, TestClock? clock = null, string viewer = "a") => new(
        state,
        new KindRegistry([new PollChoiceKind(), new PollRosterKind()]),
        clock ?? new TestClock(Started),
        viewer);

    private static SessionState State(int players, params SessionItemState[] items)
    {
        var seats = Enumerable.Range(0, players).Select(index => new Seat(((char)('a' + index)).ToString(), "player"));
        return SessionState.Create(new SessionRow("session", Started, null, null), Json("{}"), seats, items);
    }

    private static SessionItemState ChoiceItem(int slot, string kind = "poll_choice") => SessionItemState.Create(
        new SessionItem(slot, kind, 1, null, null),
        Json("{\"options\":[{\"id\":\"red\",\"text\":\"Red\"},{\"id\":\"blue\",\"text\":\"Blue\"}]}"),
        Json("{}"),
        Json("{\"options\":[{\"id\":\"red\",\"text\":\"Red\"},{\"id\":\"blue\",\"text\":\"Blue\"}]}"),
        Json("{}"));

    private static SessionItemState RosterItem(int slot) => SessionItemState.Create(
        new SessionItem(slot, "poll_roster", 1, null, null),
        Json("{}"),
        Json("{\"options\":[{\"seat\":0,\"name\":\"Ada\"},{\"seat\":1,\"name\":\"Bea\"},{\"seat\":2,\"name\":\"Cy\"}]}"),
        Json("{\"options\":[{\"seat\":0,\"name\":\"Ada\"},{\"seat\":1,\"name\":\"Bea\"},{\"seat\":2,\"name\":\"Cy\"}]}"),
        Json("{}"));

    private static Command Answer(string player, int slot, string payload, int revision = 0, DateTimeOffset? received = null) => new(
        CommandType.Answer, player, new Position(0, slot), revision, null, Json(payload), received ?? Started);
    private static Command Leave(string player, int slot) => new(
        CommandType.Leave, player, new Position(0, slot), 0, null, null, Started);

    private static PollResult Result(InMemorySession session) => JsonSerializer.Deserialize<PollResult>(session.Session.Result!.Value)
        ?? throw new InvalidOperationException("Poll result was not stored.");
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private sealed record PlainKind(string Id, string Family) : IKind
    {
        public int Version => 1;
        public bool Gradable => false;
    }
}
