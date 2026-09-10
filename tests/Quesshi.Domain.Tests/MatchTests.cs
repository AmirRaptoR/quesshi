using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchTests
{
    private const string Challenger = "u-amir";
    private const string Opponent = "u-sara";
    private const string Third = "u-vahid";
    private static readonly string[] Ten = [.. Enumerable.Range(1, MatchRules.QuestionsPerMatch).Select(i => $"q{i}")];
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static DuelSettings NewSettings() => DuelSettings.Create(Language.En, MatchRules.QuestionsPerMatch, [], []);

    /// <summary>A capacity-2 lobby with its ten-question set already drawn — what the deleted pre-lobby
    /// <c>Match.Create</c> overload used to build directly, now the settings-aware constructor plus
    /// <see cref="Match.DrawQuestions"/>.</summary>
    private static Match NewMatch()
    {
        var m = Match.Create("m1", "ABC123", Challenger, NewSettings(), capacity: 2, T0);
        m.DrawQuestions(Ten);
        return m;
    }

    /// <summary>Seated to capacity 2 and explicitly started by the owner — joining alone no longer
    /// starts a duel, so every test that wants an in-progress duel has to ask for it.</summary>
    private static Match Joined()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
        m.Start(Challenger, T0);
        return m;
    }

    /// <summary>A lobby built through the settings-aware, N-player <see cref="Match.Create"/> rather
    /// than the pre-lobby compatibility overload, with its question set already drawn.</summary>
    private static Match NewMatchN(int capacity)
    {
        var m = Match.Create("mN", "CODEN", Challenger, NewSettings(), capacity, T0);
        m.DrawQuestions(Ten);
        return m;
    }

    /// <summary>A three-player lobby, filled to capacity and explicitly started by the owner — joining
    /// alone no longer starts a duel, even once it fills every seat.</summary>
    private static Match Joined3()
    {
        var m = NewMatchN(3);
        m.Join(Opponent, T0);
        m.Join(Third, T0);
        m.Start(Challenger, T0);
        return m;
    }

    /// <summary>Plays a whole run for one player, answering correctly for the first <paramref name="correctCount"/> questions.</summary>
    private static void PlayRun(Match m, string player, int correctCount, DateTimeOffset at)
    {
        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
        {
            var served = m.ServeNext(player, at);
            m.SubmitAnswer(player, served.Index, choiceIndex: 0, correct: i < correctCount, at.AddSeconds(1));
        }
    }

    [Fact]
    public void A_new_match_waits_for_an_opponent()
    {
        var m = NewMatch();
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
        Assert.Equal([Challenger], m.Participants);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(101)]
    public void A_match_refuses_a_length_nobody_can_choose(int count)
        // DuelSettings.Create is where this validation lives now that a duel's length is settled
        // before any question set is drawn — the deleted pre-lobby Create overload used to run the
        // identical check internally, by calling straight into this same method.
        => Assert.Throws<ArgumentException>(() => DuelSettings.Create(Language.En, count, [], []));

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    public void A_run_finishes_after_however_many_questions_the_match_holds(int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"q{i}").ToList();
        var m = Match.Create("m", "C", Challenger, DuelSettings.Create(Language.En, count, [], []), capacity: 2, T0);
        m.DrawQuestions(ids);
        m.Join(Opponent, T0);

        for (var i = 0; i < count; i++)
        {
            Assert.False(m.RunOf(Challenger)?.Finished == true);
            var served = m.ServeNext(Challenger, T0);
            m.SubmitAnswer(Challenger, served.Index, 0, correct: true, T0.AddSeconds(1));
        }

        Assert.True(m.RunOf(Challenger)!.Finished);
    }

    /// <summary>
    /// A run's length is derived from the snapshot's question ids, never stored beside them — which
    /// is what lets state written before matches had a choosable length restore unchanged.
    /// </summary>
    [Fact]
    public void A_snapshot_written_without_a_run_length_restores_intact()
    {
        var m = Joined();
        PlayRun(m, Challenger, correctCount: 4, T0);

        // Round-trips through JSON exactly as the grain stores it in Redis.
        var json = System.Text.Json.JsonSerializer.Serialize(m.ToSnapshot());
        var restored = Match.FromSnapshot(System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(json)!);

        Assert.True(restored.RunOf(Challenger)!.Finished);
        Assert.Equal(4, restored.RunOf(Challenger)!.Correct);
        Assert.Null(restored.RunOf(Opponent));
    }

    [Fact]
    public void The_challenger_can_play_before_anyone_joins()
    {
        var m = NewMatch();
        PlayRun(m, Challenger, correctCount: 6, T0);
        Assert.True(m.RunOf(Challenger)!.Finished);
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
    }

    [Fact]
    public void The_challenger_cannot_join_their_own_match()
        => Assert.Throws<InvalidOperationException>(() => NewMatch().Join(Challenger, T0));

    [Fact]
    public void A_third_player_cannot_join_a_taken_match()
        => Assert.Throws<InvalidOperationException>(() => Joined().Join("u-else", T0));

    /// <summary>
    /// The core of issue #104: a two-seat lobby used to start the instant its second seat filled. It
    /// no longer does; the owner's <see cref="Match.Start"/> is the only door into
    /// <see cref="MatchState.InProgress"/>, for a two-seat lobby exactly as for a bigger one.
    /// </summary>
    [Fact]
    public void Join_leaves_a_two_seat_lobby_open_once_it_is_full()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);

        Assert.Equal(MatchState.AwaitingOpponent, m.State);
        Assert.Equal([Challenger, Opponent], m.Participants);
    }

    [Fact]
    public void A_third_join_into_a_full_two_seat_lobby_is_refused()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);

        Assert.Throws<InvalidOperationException>(() => m.Join(Third, T0));
        Assert.Equal(2, m.Participants.Count);
    }

    [Fact]
    public void A_full_lobby_reopens_once_a_non_owner_leaves_and_the_owner_can_still_start_it()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
        Assert.Throws<InvalidOperationException>(() => m.Join(Third, T0)); // confirm it is actually full first

        Assert.True(m.Leave(Opponent, T0));
        m.Join(Third, T0);
        Assert.Equal([Challenger, Third], m.Participants);
        Assert.True(m.Start(Challenger, T0));
        Assert.Equal(MatchState.InProgress, m.State);
    }

    [Fact]
    public void A_stranger_cannot_play_a_match_they_are_not_in()
        => Assert.Throws<InvalidOperationException>(() => Joined().ServeNext("u-else", T0));

    [Fact]
    public void Answering_a_question_that_was_never_served_is_rejected()
        => Assert.Throws<InvalidOperationException>(
            () => Joined().SubmitAnswer(Challenger, 0, choiceIndex: 0, correct: true, T0));

    [Fact]
    public void Answering_out_of_order_is_rejected()
    {
        var m = Joined();
        m.ServeNext(Challenger, T0);
        Assert.Throws<InvalidOperationException>(
            () => m.SubmitAnswer(Challenger, 3, choiceIndex: 0, correct: true, T0));
    }

    [Fact]
    public void Answering_the_same_question_twice_is_rejected()
    {
        var m = Joined();
        var served = m.ServeNext(Challenger, T0);
        m.SubmitAnswer(Challenger, served.Index, 0, true, T0);
        Assert.Throws<InvalidOperationException>(() => m.SubmitAnswer(Challenger, served.Index, 0, true, T0));
    }

    [Fact]
    public void Playing_on_after_finishing_the_run_is_rejected()
    {
        var m = Joined();
        PlayRun(m, Challenger, 6, T0);
        Assert.Throws<InvalidOperationException>(() => m.ServeNext(Challenger, T0));
    }

    [Fact]
    public void Both_players_face_the_same_questions_in_the_same_order()
    {
        var m = Joined();
        var mine = new List<string>();
        var theirs = new List<string>();
        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
        {
            mine.Add(m.ServeNext(Challenger, T0).QuestionId);
            m.SubmitAnswer(Challenger, i, 0, true, T0);
            theirs.Add(m.ServeNext(Opponent, T0).QuestionId);
            m.SubmitAnswer(Opponent, i, 0, true, T0);
        }
        Assert.Equal(mine, theirs);
    }

    [Fact]
    public void A_players_answers_stay_hidden_until_the_other_has_finished()
    {
        var m = Joined();
        PlayRun(m, Challenger, 6, T0);
        Assert.False(m.CanReveal(Opponent), "opponent has not played yet");
        PlayRun(m, Opponent, 3, T0);
        Assert.True(m.CanReveal(Opponent));
        Assert.True(m.CanReveal(Challenger));
    }

    [Fact]
    public void The_match_resolves_when_both_runs_are_finished()
    {
        var m = Joined();
        PlayRun(m, Challenger, 6, T0);
        PlayRun(m, Opponent, 3, T0);
        Assert.Equal(MatchState.Resolved, m.State);
        Assert.Equal(Challenger, m.WinnerId);
    }

    [Fact]
    public void Equal_scores_resolve_to_a_draw()
    {
        var m = Joined();
        PlayRun(m, Challenger, 4, T0);
        PlayRun(m, Opponent, 4, T0);
        Assert.Equal(MatchState.Resolved, m.State);
        Assert.Null(m.WinnerId);
        Assert.True(m.IsDraw);
    }

    [Fact]
    public void A_late_answer_scores_nothing_but_still_advances_the_run()
    {
        var m = Joined();
        var served = m.ServeNext(Challenger, T0);
        m.SubmitAnswer(Challenger, served.Index, 0, correct: true, T0 + MatchRules.QuestionTime + TimeSpan.FromMinutes(1));
        var run = m.RunOf(Challenger)!;
        Assert.Equal(0, run.Score);
        Assert.Single(run.Answers);
    }

    [Fact]
    public void An_untouched_match_forfeits_after_the_deadline()
    {
        var m = Joined();
        PlayRun(m, Challenger, 5, T0);
        Assert.False(m.TryForfeit(T0 + MatchRules.ForfeitAfter - TimeSpan.FromHours(1)));
        Assert.True(m.TryForfeit(T0 + MatchRules.ForfeitAfter + TimeSpan.FromMinutes(1)));
        Assert.Equal(MatchState.Forfeited, m.State);
        Assert.Equal(Challenger, m.WinnerId);
    }

    [Fact]
    public void A_resolved_match_cannot_be_forfeited()
    {
        var m = Joined();
        PlayRun(m, Challenger, 6, T0);
        PlayRun(m, Opponent, 1, T0);
        Assert.False(m.TryForfeit(T0 + MatchRules.ForfeitAfter * 2));
        Assert.Equal(MatchState.Resolved, m.State);
    }

    [Fact]
    public void Nobody_played_so_a_forfeit_leaves_no_winner()
    {
        var m = Joined();
        Assert.True(m.TryForfeit(T0 + MatchRules.ForfeitAfter + TimeSpan.FromMinutes(1)));
        Assert.Null(m.WinnerId);
    }

    // ---- Participants, settings and DrawQuestions (N players) ----

    [Fact]
    public void A_fresh_lobby_holds_only_its_owner()
    {
        var m = NewMatchN(3);
        Assert.Equal([Challenger], m.Participants);
        Assert.Equal(Challenger, m.OwnerId);
        Assert.Equal(3, m.Capacity);
        Assert.Empty(m.Standings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Create_rejects_a_capacity_outside_two_to_eight(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Match.Create("m", "C", Challenger, NewSettings(), capacity, T0));

    [Fact]
    public void ServeNext_is_refused_while_the_question_set_is_undrawn()
    {
        var m = Match.Create("m", "C", Challenger, NewSettings(), 2, T0); // no DrawQuestions call
        Assert.Throws<InvalidOperationException>(() => m.ServeNext(Challenger, T0));
    }

    [Fact]
    public void DrawQuestions_cannot_be_called_once_the_duel_has_started()
    {
        var m = Joined3(); // capacity 3, fully joined -- already InProgress
        Assert.Throws<InvalidOperationException>(() => m.DrawQuestions(Ten));
    }

    // ---- Start, Leave, Cancel and UpdateSettings ----

    [Fact]
    public void Start_by_the_owner_with_two_seated_begins_the_duel_with_room_to_spare()
    {
        var m = NewMatchN(3); // already drawn
        m.Join(Opponent, T0);

        Assert.True(m.Start(Challenger, T0));
        Assert.Equal(MatchState.InProgress, m.State);
    }

    [Fact]
    public void Start_is_refused_below_two_participants()
    {
        var m = NewMatchN(3);
        Assert.False(m.Start(Challenger, T0));
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
    }

    [Fact]
    public void Start_is_refused_for_a_non_owner()
    {
        var m = NewMatchN(3);
        m.Join(Opponent, T0);
        Assert.False(m.Start(Opponent, T0));
    }

    [Fact]
    public void Start_is_refused_once_the_lobby_has_already_started()
    {
        var m = Joined3();
        Assert.False(m.Start(Challenger, T0));
    }

    [Fact]
    public void Start_is_refused_while_the_question_set_is_still_undrawn()
    {
        var m = Match.Create("m", "C", Challenger, NewSettings(), 3, T0); // no DrawQuestions call
        m.Join(Opponent, T0);
        Assert.False(m.Start(Challenger, T0));
    }

    [Fact]
    public void Leave_frees_a_non_owner_seat()
    {
        var m = NewMatchN(4); // room to spare after two join, so leaving still leaves it short of Capacity
        m.Join(Opponent, T0);
        m.Join(Third, T0);

        Assert.True(m.Leave(Opponent, T0));
        Assert.Equal([Challenger, Third], m.Participants);
        Assert.Equal(MatchState.AwaitingOpponent, m.State); // still short of Capacity
    }

    [Fact]
    public void Leave_is_refused_for_the_owner()
    {
        var m = NewMatchN(3);
        Assert.False(m.Leave(Challenger, T0));
        Assert.Equal([Challenger], m.Participants);
    }

    [Fact]
    public void Leave_is_refused_once_the_duel_has_started()
    {
        var m = Joined3();
        Assert.False(m.Leave(Opponent, T0));
    }

    [Fact]
    public void Cancel_by_the_owner_ends_the_lobby_as_no_contest_with_no_ownership_transfer()
    {
        var m = NewMatchN(3);
        m.Join(Opponent, T0);

        Assert.True(m.Cancel(Challenger, T0));
        Assert.Equal(MatchState.NoContest, m.State);
        Assert.True(m.IsOver);
        Assert.Null(m.WinnerId);
        Assert.False(m.IsDraw);
        Assert.Empty(m.Standings); // nobody is credited
        Assert.Equal(Challenger, m.OwnerId); // no ownership transfer
    }

    [Fact]
    public void Cancel_is_refused_for_a_non_owner()
    {
        var m = NewMatchN(3);
        m.Join(Opponent, T0);
        Assert.False(m.Cancel(Opponent, T0));
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
    }

    [Fact]
    public void Cancel_is_refused_once_the_duel_has_started()
    {
        var m = Joined3();
        Assert.False(m.Cancel(Challenger, T0));
    }

    [Fact]
    public void UpdateSettings_changes_settings_while_the_question_set_is_still_empty()
    {
        var m = Match.Create("m", "C", Challenger, NewSettings(), 3, T0);
        var next = DuelSettings.Create(Language.Fa, 20, ["geography"], [Difficulty.Hard]);

        Assert.True(m.UpdateSettings(Challenger, next));
        Assert.Equal(next, m.Settings);
    }

    [Fact]
    public void UpdateSettings_is_refused_once_the_question_set_is_drawn()
    {
        var m = NewMatchN(3); // NewMatchN already draws
        var original = m.Settings;

        Assert.False(m.UpdateSettings(Challenger, DuelSettings.Create(Language.Fa, 20, [], [])));
        Assert.Equal(original, m.Settings);
    }

    [Fact]
    public void UpdateSettings_is_refused_for_a_non_owner()
    {
        var m = Match.Create("m", "C", Challenger, NewSettings(), 3, T0);
        var original = m.Settings;

        Assert.False(m.UpdateSettings(Opponent, DuelSettings.Create(Language.Fa, 20, [], [])));
        Assert.Equal(original, m.Settings);
    }

    // ---- SetCapacity ----

    [Fact]
    public void The_owner_widens_capacity_in_the_lobby_phase()
    {
        var m = NewMatch();
        Assert.True(m.CanSetCapacity(Challenger, 4));
        Assert.True(m.SetCapacity(Challenger, 4, T0));
        Assert.Equal(4, m.Capacity);
    }

    [Fact]
    public void SetCapacity_is_refused_for_a_non_owner()
    {
        var m = NewMatch();
        Assert.False(m.CanSetCapacity(Opponent, 4));
        Assert.False(m.SetCapacity(Opponent, 4, T0));
        Assert.Equal(2, m.Capacity);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void SetCapacity_is_refused_outside_two_to_eight(int capacity)
    {
        var m = NewMatch();
        Assert.False(m.CanSetCapacity(Challenger, capacity));
        Assert.False(m.SetCapacity(Challenger, capacity, T0));
        Assert.Equal(2, m.Capacity);
    }

    [Fact]
    public void SetCapacity_is_refused_below_the_seated_count()
    {
        var m = NewMatchN(4);
        m.Join(Opponent, T0);
        m.Join(Third, T0); // 3 seated

        Assert.False(m.CanSetCapacity(Challenger, 2));
        Assert.False(m.SetCapacity(Challenger, 2, T0));
        Assert.Equal(4, m.Capacity);
    }

    [Fact]
    public void SetCapacity_is_refused_once_the_duel_has_started()
    {
        var m = Joined(); // capacity 2, already started
        Assert.False(m.CanSetCapacity(Challenger, 4));
        Assert.False(m.SetCapacity(Challenger, 4, T0));
        Assert.Equal(2, m.Capacity);
    }

    // ---- StartByPairing ----

    /// <summary>
    /// The random-matchmaking door into <c>BeginDuel</c>, once <see cref="Join"/> no longer auto-starts
    /// on its own: no owner check, since the joiner triggers this, not the owner pressing Start.
    /// </summary>
    [Fact]
    public void StartByPairing_begins_the_duel_once_two_are_seated_and_questions_are_drawn()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);

        Assert.True(m.StartByPairing(T0));
        Assert.Equal(MatchState.InProgress, m.State);
    }

    [Fact]
    public void StartByPairing_is_refused_below_two_participants()
    {
        var m = NewMatch();
        Assert.False(m.StartByPairing(T0));
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
    }

    [Fact]
    public void StartByPairing_is_refused_once_the_lobby_has_already_started()
    {
        var m = Joined();
        Assert.False(m.StartByPairing(T0));
    }

    [Fact]
    public void A_widened_lobby_can_seat_more_than_the_original_capacity()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
        Assert.True(m.SetCapacity(Challenger, 4, T0));

        m.Join(Third, T0);
        Assert.Equal([Challenger, Opponent, Third], m.Participants);
        Assert.Equal(MatchState.AwaitingOpponent, m.State); // still short of the new capacity
    }

    // ---- N-player standings ----

    [Fact]
    public void A_lobby_with_room_for_more_does_not_resolve_until_every_seat_is_finished_playing()
    {
        var m = Joined3();
        PlayRun(m, Challenger, 6, T0);
        Assert.Equal(MatchState.InProgress, m.State); // still waiting on the other two seats
        PlayRun(m, Opponent, 5, T0);
        Assert.Equal(MatchState.InProgress, m.State); // still waiting on the last seat

        PlayRun(m, Third, 4, T0);
        Assert.Equal(MatchState.Resolved, m.State); // resolves the instant the last run finishes
    }

    [Fact]
    public void Standings_share_first_place_when_two_finishers_tie_at_the_top()
    {
        var m = Joined3();

        // Challenger and Opponent play identically (same correct count, same timing) for an exact
        // tie; Third plays fewer correctly for a strictly lower score.
        PlayRun(m, Challenger, 8, T0);
        PlayRun(m, Opponent, 8, T0);
        PlayRun(m, Third, 3, T0);

        Assert.Equal(MatchState.Resolved, m.State);
        Assert.Null(m.WinnerId); // nobody won outright
        Assert.True(m.IsDraw); // means "no sole winner", not "everybody drew"

        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(1, byId[Challenger].Place);
        Assert.Equal(1, byId[Opponent].Place);
        Assert.Equal(MatchOutcome.Draw, byId[Challenger].Outcome);
        Assert.Equal(MatchOutcome.Draw, byId[Opponent].Outcome);
        Assert.Equal(byId[Challenger].Score, byId[Opponent].Score);
        Assert.Equal(3, byId[Third].Place); // competition ranking: the tie for first skips place 2
        Assert.Equal(MatchOutcome.Loss, byId[Third].Outcome);
        Assert.True(byId[Challenger].Score > byId[Third].Score);
    }

    [Fact]
    public void Standings_give_a_sole_winner_when_nobody_ties_the_top_score()
    {
        var m = Joined3();
        PlayRun(m, Challenger, 9, T0);
        PlayRun(m, Opponent, 6, T0);
        PlayRun(m, Third, 3, T0);

        Assert.Equal(Challenger, m.WinnerId);
        Assert.False(m.IsDraw);

        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(1, byId[Challenger].Place);
        Assert.Equal(MatchOutcome.Win, byId[Challenger].Outcome);
        Assert.Equal(2, byId[Opponent].Place);
        Assert.Equal(MatchOutcome.Loss, byId[Opponent].Outcome);
        Assert.Equal(3, byId[Third].Place);
        Assert.Equal(MatchOutcome.Loss, byId[Third].Outcome);
    }

    [Fact]
    public void Forfeiture_finalises_standings_from_whatever_each_player_banked()
    {
        var m = Joined3();
        PlayRun(m, Challenger, 6, T0); // finishes a full, real run

        // Opponent banks one correct answer and then goes quiet -- an unfinished run that must still
        // be scored, not treated as "still playing" once the match is over.
        var served = m.ServeNext(Opponent, T0);
        m.SubmitAnswer(Opponent, served.Index, 0, correct: true, T0.AddSeconds(1));

        // Third never plays a single question.

        var forfeitAt = T0 + MatchRules.ForfeitAfter + TimeSpan.FromMinutes(1);
        Assert.True(m.TryForfeit(forfeitAt));
        Assert.Equal(MatchState.Forfeited, m.State);

        // Every participant is finalised -- none is left "still playing" just because their run
        // never finished; a forfeited match never will let it.
        Assert.Equal(3, m.Standings.Count);

        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(Challenger, m.WinnerId);
        Assert.Equal(1, byId[Challenger].Place);
        Assert.Equal(MatchOutcome.Win, byId[Challenger].Outcome);
        Assert.True(byId[Challenger].Score > byId[Opponent].Score);
        Assert.True(byId[Opponent].Score > 0);
        Assert.Equal(0, byId[Third].Score);
        Assert.Equal(3, byId[Third].Place);
    }

    // ---- The three missing async guards ----

    [Fact]
    public void SubmitAnswer_refuses_an_answer_once_the_match_is_terminal()
    {
        var m = Joined();
        // Served before the forfeit, submitted after it -- the exact race the terminal check exists
        // to close: without it this would still mutate the run and re-run TryResolve on a match the
        // caller already believes is over.
        var served = m.ServeNext(Challenger, T0);
        Assert.True(m.TryForfeit(T0 + MatchRules.ForfeitAfter + TimeSpan.FromMinutes(1)));

        Assert.Throws<InvalidOperationException>(() =>
            m.SubmitAnswer(Challenger, served.Index, 0, correct: true, T0 + MatchRules.ForfeitAfter + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Join_settles_the_clock_first_so_an_expired_lobby_cannot_be_joined()
    {
        var m = NewMatch();
        var pastDeadline = T0 + MatchRules.ForfeitAfter + TimeSpan.FromMinutes(1);

        // Join must call TryForfeit(now) itself -- without it, the 48-hour deadline would rest
        // solely on the grain's reminder, and a late or lost reminder would leave this lobby
        // joinable forever.
        Assert.Throws<InvalidOperationException>(() => m.Join(Opponent, pastDeadline));
        Assert.Equal(MatchState.Forfeited, m.State);
    }

    [Fact]
    public void IsOver_includes_no_contest_so_a_cancelled_lobby_reads_as_over()
    {
        // Nothing in Match yet produces NoContest on its own -- owner-cancellation is grain-layer
        // work for a later step of issue #47 -- but a persisted record can already carry it, and
        // IsOver must not misreport that record as still running.
        var snapshot = new MatchSnapshot(
            "m1", "ABC123", [Challenger], 2, NewSettings(), [.. Ten], MatchState.NoContest, T0, T0, null, false,
            new Dictionary<string, RunSnapshot>(), []);
        Assert.True(Match.FromSnapshot(snapshot).IsOver);
    }

    // ---- Migration: tolerating a snapshot written before Participants/Settings existed ----

    /// <summary>
    /// Exactly the JSON a match written before this migration produces — literal text, not a value
    /// built from today's <see cref="MatchSnapshot"/> and then trimmed, because the whole point of this
    /// suite is proving the *actual* old wire shape still deserializes, not a shape this test assumes.
    /// This is what <c>MatchSnapshot</c> looked like start to finish before <c>Participants</c>,
    /// <c>Capacity</c>, <c>Settings</c> and <c>Standings</c> existed: <c>Lang</c>, <c>ChallengerId</c>,
    /// <c>OpponentId</c> in their place.
    /// </summary>
    private static string LegacyJson(string opponentIdJson, string state = "1") =>
        $$"""
        {"Id":"legacy-1","Code":"OLD001","Lang":1,"ChallengerId":"u-legacy-challenger","OpponentId":{{opponentIdJson}},
         "QuestionIds":["lq1","lq2","lq3","lq4","lq5","lq6","lq7","lq8","lq9","lq10"],"State":{{state}},
         "CreatedAt":"2026-08-19T00:00:00+00:00","EndedAt":null,"WinnerId":null,"IsDraw":false,"Runs":{} }
        """;

    [Fact]
    public void A_legacy_snapshot_deserializes_and_restores_the_two_player_shape()
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(LegacyJson("\"u-legacy-opponent\""))!;

        // The tell that this blob predates Participants: the field itself never appears in the JSON
        // above, so System.Text.Json leaves the constructor argument at its default rather than
        // throwing -- and Lang/ChallengerId/OpponentId land in the three legacy fields instead.
        Assert.Null(snapshot.Participants);
        Assert.Equal("u-legacy-challenger", snapshot.ChallengerId);

        var m = Match.FromSnapshot(snapshot);

        Assert.Equal(["u-legacy-challenger", "u-legacy-opponent"], m.Participants);
        Assert.Equal("u-legacy-challenger", m.OwnerId);
        Assert.Equal(2, m.Capacity);
        Assert.Equal(Language.En, m.Settings.Language);
        Assert.Equal(10, m.Settings.QuestionCount);
        Assert.Empty(m.Settings.CategoryIds);
        Assert.Empty(m.Settings.Levels);
        Assert.Equal(10, m.QuestionIds.Count);
    }

    [Fact]
    public void A_legacy_lobby_nobody_joined_keeps_its_free_seat()
    {
        // AwaitingOpponent (State: 0) with questions already drawn is exactly the subtle case the
        // migration exists for: ServeNext/SubmitAnswer never required InProgress, so the challenger
        // could have played their whole run alone before anyone joined.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(LegacyJson("null", state: "0"))!;
        var m = Match.FromSnapshot(snapshot);

        Assert.Equal(["u-legacy-challenger"], m.Participants);

        // The free seat still takes a joiner.
        m.Join(Opponent, T0);
        Assert.Equal(["u-legacy-challenger", Opponent], m.Participants);
    }

    [Fact]
    public void A_legacy_records_settings_are_read_only_because_its_questions_are_already_drawn()
    {
        // "Settings are editable exactly while QuestionIds is empty" is the rule that makes a legacy
        // record's settings read-only without any new state: its questions arrived already drawn, so
        // DrawQuestions refuses exactly as it would for any other match past Start.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(LegacyJson("null", state: "0"))!;
        var m = Match.FromSnapshot(snapshot);

        Assert.Throws<InvalidOperationException>(() => m.DrawQuestions(Ten));
    }

    [Fact]
    public void Both_snapshot_shapes_round_trip_through_JSON()
    {
        var legacy = Match.FromSnapshot(System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(LegacyJson("\"u-legacy-opponent\""))!);
        var freshlyWritten = System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(legacy.ToSnapshot()))!;

        // A record this code writes never carries the legacy fields, even immediately after loading
        // one that did -- ToSnapshot only ever emits the new shape.
        Assert.Null(freshlyWritten.ChallengerId);
        Assert.Null(freshlyWritten.Lang);
        Assert.Equal(["u-legacy-challenger", "u-legacy-opponent"], freshlyWritten.Participants);

        var restored = Match.FromSnapshot(freshlyWritten);
        Assert.Equal(legacy.Participants, restored.Participants);
        // DuelSettings' own record equality compares CategoryIds/Levels by reference through their
        // IReadOnlyList<T> field type, which a JSON round trip never preserves (an array in, a List<T>
        // out) -- so this compares the values that actually matter instead of the whole record.
        Assert.Equal(legacy.Settings.Language, restored.Settings.Language);
        Assert.Equal(legacy.Settings.QuestionCount, restored.Settings.QuestionCount);

        // The N-player shape itself round-trips unchanged -- this is the safety net the domain step
        // already relies on, exercised here through the same JSON path the grain actually uses.
        var m3 = Joined3();
        var restored3 = Match.FromSnapshot(System.Text.Json.JsonSerializer.Deserialize<MatchSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(m3.ToSnapshot()))!);
        Assert.Equal(m3.Participants, restored3.Participants);
        Assert.Equal(m3.Capacity, restored3.Capacity);
    }
}
