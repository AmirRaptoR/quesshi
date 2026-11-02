using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Flashcards;
using Quesshi.Formats.Testing;
using Quesshi.Shared;

namespace Quesshi.Formats.Tests;

public sealed class FlashcardsFormatTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Flashcards_is_registered_with_the_declared_solo_personal_contract()
    {
        IFormat format = PlatformFormats.CreateRegistry().Get("flashcards");

        Assert.Equal(Pacing.Personal, format.Spec.Pacing);
        Assert.Equal(1, format.Spec.MinPlayers);
        Assert.Equal(1, format.Spec.MaxPlayers);
        Assert.False(format.Spec.Lobby);
        Assert.False(format.Spec.Scored);
        Assert.True(format.Spec.Guests);
        Assert.Null(format.Spec.Matchmaking);
        Assert.False(format.Spec.Rematch);
        Assert.True(format.Spec.Accepts(new PlainKind("flashcard", "card")));
        Assert.True(format.Spec.Accepts(new PlainKind("other_card", "card")));
        Assert.False(format.Spec.Accepts(new PlainKind("choice", "trivia")));
        Assert.False(format.Spec.Accepts(new PlainKind("poll_choice", "poll")));
        Assert.Equal(["flashcard"], format.Plan(new ContentSpec("pack", Json("{}"), 3), Json("{}")).AcceptedKinds);
    }

    [Fact]
    public void Start_serves_slot_zero_on_the_front_with_a_deadline_thirty_days_ahead()
    {
        var session = Harness(Deck(3));

        new FlashcardsFormat().Start(session);

        Assert.Equal(new Position(FlashcardsFormat.FrontPhase, 0), session.Seats.Single().Position);
        Assert.Equal(Started.AddDays(30), session.Session.DueAt);
    }

    [Fact]
    public void The_view_before_a_flip_holds_the_front_and_no_back_text()
    {
        var format = new FlashcardsFormat();
        var session = Harness(Deck(2));
        format.Start(session);

        var view = Assert.IsType<FlashcardsRunningView>(format.View(session));

        Assert.Equal((0, "front", 2, 0), (view.Slot, view.Side, view.Total, view.Rated));
        Assert.Null(view.Back);
        Assert.DoesNotContain("back-0", JsonSerializer.Serialize(view), StringComparison.Ordinal);
        Assert.Contains("front-0", view.Card.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_flip_releases_the_back_at_the_same_slot_and_a_repeated_flip_changes_nothing()
    {
        var format = new FlashcardsFormat();
        var session = Harness(Deck(2));
        format.Start(session);

        format.Apply(session, Flip(0));
        var flipped = session.CurrentState;
        var view = Assert.IsType<FlashcardsRunningView>(format.View(session));

        Assert.Equal((0, "back"), (view.Slot, view.Side));
        Assert.Contains("back-0", view.Back!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, session.Seats.Single().ReleasedSlot);

        format.Apply(session, Flip(0));
        format.Apply(session, new Command(CommandType.Action, "a", new Position(FlashcardsFormat.BackPhase, 0), 0, "flip", null, Started));
        Assert.Same(flipped, session.CurrentState);
    }

    [Fact]
    public void A_rating_on_the_front_is_refused_as_stale_and_records_nothing()
    {
        var format = new FlashcardsFormat();
        var session = Harness(Deck(2));
        format.Start(session);
        var before = session.CurrentState;

        var refused = Assert.Throws<SessionCommandException>(() => format.Apply(session, Rate(FlashcardsFormat.FrontPhase, 0, 2)));

        Assert.Equal("stale_position", refused.Code);
        Assert.Same(before, session.CurrentState);
        Assert.Empty(session.CurrentState.Answers);
    }

    [Fact]
    public void A_rating_on_the_back_advances_to_the_next_front_and_a_repeat_is_harmless()
    {
        var format = new FlashcardsFormat();
        var session = Harness(Deck(2));
        format.Start(session);
        format.Apply(session, Flip(0));

        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 0, 3));
        var advanced = session.CurrentState;
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 0, 1));

        Assert.Same(advanced, session.CurrentState);
        Assert.Equal(new Position(FlashcardsFormat.FrontPhase, 1), session.Seats.Single().Position);
        Assert.Single(session.CurrentState.Answers);
        var view = Assert.IsType<FlashcardsRunningView>(format.View(session));
        Assert.Equal((1, "front", 1), (view.Slot, view.Side, view.Rated));
        Assert.Null(view.Back);
    }

    [Fact]
    public void Rating_the_last_card_completes_the_session_with_the_count_per_rating()
    {
        var format = new FlashcardsFormat();
        var session = Harness(Deck(3));
        format.Start(session);
        foreach (var rating in new[] { 3, 0, 3 })
        {
            var slot = session.Seats.Single().Position!.Slot;
            format.Apply(session, Flip(slot));
            format.Apply(session, Rate(FlashcardsFormat.BackPhase, slot, rating));
        }

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        Assert.Equal([1, 0, 0, 2], Counts(session));
        Assert.Null(session.Session.DueAt);
        var view = Assert.IsType<FlashcardsFinishedView>(format.View(session));
        Assert.Equal([1, 0, 0, 2], view.Counts);
        Assert.Equal(3, view.Total);
    }

    [Fact]
    public void A_flip_and_a_rating_each_move_the_deadline_thirty_days_ahead_of_the_action_but_never_past_ninety_days()
    {
        var clock = new TestClock(Started);
        var format = new FlashcardsFormat();
        var session = Harness(Deck(4), clock);
        format.Start(session);

        clock.Set(Started.AddDays(20));
        format.Apply(session, Flip(0));
        Assert.Equal(Started.AddDays(50), session.Session.DueAt);

        clock.Set(Started.AddDays(45));
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 0, 2));
        Assert.Equal(Started.AddDays(75), session.Session.DueAt);

        clock.Set(Started.AddDays(70));
        format.Apply(session, Flip(1));
        Assert.Equal(Started.AddDays(90), session.Session.DueAt);

        clock.Set(Started.AddDays(80));
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 1, 2));
        Assert.Equal(Started.AddDays(90), session.Session.DueAt);
    }

    [Fact]
    public void At_the_deadline_an_untouched_session_completes_with_zero_ratings()
    {
        var clock = new TestClock(Started);
        var format = new FlashcardsFormat();
        var session = Harness(Deck(2), clock);
        format.Start(session);

        clock.Set(Started.AddDays(30).AddTicks(-1));
        format.Timeout(session);
        Assert.False(session.Session.Ended);

        clock.Set(Started.AddDays(30));
        format.Timeout(session);

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        Assert.Equal([0, 0, 0, 0], Counts(session));
    }

    [Fact]
    public void At_the_deadline_a_partly_rated_session_counts_its_ratings_and_a_flip_on_day_29_keeps_it_running_until_day_59()
    {
        var clock = new TestClock(Started);
        var format = new FlashcardsFormat();
        var session = Harness(Deck(4), clock);
        format.Start(session);
        format.Apply(session, Flip(0));
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 0, 1));
        format.Apply(session, Flip(1));
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 1, 1));
        format.Apply(session, Flip(2));
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 2, 3));

        clock.Set(Started.AddDays(29));
        format.Apply(session, Flip(3));
        clock.Set(Started.AddDays(58));
        format.Timeout(session);
        Assert.False(session.Session.Ended);

        clock.Set(Started.AddDays(59));
        format.Timeout(session);

        Assert.True(session.Session.Ended);
        Assert.Equal(EndReason.Completed, session.Session.EndReason);
        Assert.Equal([0, 2, 0, 1], Counts(session));
    }

    [Fact]
    public void Reloading_mid_deck_returns_the_same_card_and_side()
    {
        var clock = new TestClock(Started);
        var format = new FlashcardsFormat();
        var session = Harness(Deck(3), clock);
        format.Start(session);
        format.Apply(session, Flip(0));
        format.Apply(session, Rate(FlashcardsFormat.BackPhase, 0, 2));
        format.Apply(session, Flip(1));
        var before = JsonSerializer.Serialize(format.View(session));

        clock.Set(Started.AddDays(9));
        var reloaded = Harness(session.CurrentState, clock);

        Assert.Equal(before, JsonSerializer.Serialize(format.View(reloaded)));
        Assert.Equal("back", Assert.IsType<FlashcardsRunningView>(format.View(reloaded)).Side);
    }

    [Fact]
    public void A_stranger_gets_the_unavailable_view_and_cannot_move_the_deck()
    {
        var format = new FlashcardsFormat();
        var session = Harness(Deck(2), viewer: "outsider");
        format.Start(session);
        var before = session.CurrentState;

        format.Apply(session, new Command(CommandType.Action, "outsider", new Position(0, 0), 0, "flip", null, Started));

        Assert.IsType<FlashcardsUnavailableView>(format.View(session));
        Assert.Same(before, session.CurrentState);
    }

    private static long[] Counts(InMemorySession session) =>
        JsonSerializer.Deserialize<FlashcardsResult>(session.Session.Result!.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Counts.Select(count => (long)count).ToArray();

    private static InMemorySession Harness(SessionState state, TestClock? clock = null, string viewer = "a") => new(
        state, new KindRegistry([new TestFlashcardKind()]), clock ?? new TestClock(Started), viewer);

    private static SessionState Deck(int cards) => SessionState.Create(
        new SessionRow("session", Started, null, null),
        Json("{}"),
        [new Seat("a", "player")],
        Enumerable.Range(0, cards).Select(slot => SessionItemState.Create(
            new SessionItem(slot, "flashcard", 1, null, null),
            Json($"{{\"front\":\"front-{slot}\",\"back\":\"back-{slot}\"}}"),
            Json("{}"),
            Json($"{{\"front\":\"front-{slot}\"}}"),
            Json($"{{\"back\":\"back-{slot}\"}}"))));

    private static Command Flip(int slot) => new(CommandType.Action, "a", new Position(FlashcardsFormat.FrontPhase, slot), 0, "flip", null, Started);
    private static Command Rate(int phase, int slot, int rating) => new(
        CommandType.Answer, "a", new Position(phase, slot), 0, null, Json($"{{\"rating\":{rating}}}"), Started);
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private sealed record PlainKind(string Id, string Family) : IKind
    {
        public int Version => 1;
        public bool Gradable => false;
    }
}
