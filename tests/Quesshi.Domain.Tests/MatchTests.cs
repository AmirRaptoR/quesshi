using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchTests
{
    private const string Challenger = "u-amir";
    private const string Opponent = "u-sara";
    private static readonly string[] Ten = [.. Enumerable.Range(1, MatchRules.QuestionsPerMatch).Select(i => $"q{i}")];
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static Match NewMatch() => Match.Create("m1", "ABC123", Language.En, Challenger, Ten, T0);

    private static Match Joined()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
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
}
