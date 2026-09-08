using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

// This file exercises the temporary compatibility adapters (ChallengerId, OpponentId, and the
// pre-lobby Create overload) deliberately and extensively — they are load-bearing for MatchGrain
// until issue #47's later steps migrate it, and issue #56 deletes them. One pragma for the whole
// file beats sprinkling it around every assertion that touches one.
#pragma warning disable CS0618

public class MatchTests
{
    private const string Challenger = "u-amir";
    private const string Opponent = "u-sara";
    private const string Third = "u-vahid";
    private static readonly string[] Ten = [.. Enumerable.Range(1, MatchRules.QuestionsPerMatch).Select(i => $"q{i}")];
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static DuelSettings NewSettings() => DuelSettings.Create(Language.En, MatchRules.QuestionsPerMatch, [], []);

    private static Match NewMatch() => Match.Create("m1", "ABC123", Language.En, Challenger, Ten, T0);

    private static Match Joined()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
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

    /// <summary>A three-player lobby, filled to capacity so it has started.</summary>
    private static Match Joined3()
    {
        var m = NewMatchN(3);
        m.Join(Opponent, T0);
        m.Join(Third, T0);
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
        Assert.Null(m.OpponentId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(101)]
    public void A_match_refuses_a_length_nobody_can_choose(int count)
        => Assert.Throws<ArgumentException>(() =>
            Match.Create("m", "C", Language.En, Challenger, [.. Enumerable.Range(0, count).Select(i => $"q{i}")], T0));

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    public void A_run_finishes_after_however_many_questions_the_match_holds(int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"q{i}").ToList();
        var m = Match.Create("m", "C", Language.En, Challenger, ids, T0);
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

    [Fact]
    public void ChallengerId_and_OpponentId_proxy_the_owner_and_the_second_seat()
    {
        var m = NewMatchN(2);
        Assert.Equal(m.OwnerId, m.ChallengerId);
        Assert.Null(m.OpponentId);

        m.Join(Opponent, T0);
        Assert.Equal(Opponent, m.OpponentId);
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
}
