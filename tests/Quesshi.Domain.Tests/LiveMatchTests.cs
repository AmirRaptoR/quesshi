using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class LiveMatchTests
{
    private const string Challenger = "u-amir";
    private const string Opponent = "u-sara";
    private static readonly string[] Ten = [.. Enumerable.Range(1, MatchRules.QuestionsPerMatch).Select(i => $"q{i}")];
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static LiveMatch NewMatch() => LiveMatch.Create("lm1", Challenger, Ten, T0);

    private static LiveMatch Joined()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
        return m;
    }

    /// <summary>Joined and past the countdown, so round 0 is open for answers.</summary>
    private static LiveMatch InRound0()
    {
        var m = Joined();
        m.Advance(T0 + LiveRules.StartCountdown);
        return m;
    }

    // ---- Shape ----

    [Fact]
    public void MatchState_gains_abandoned_and_no_contest_appended()
    {
        Assert.Equal(4, (int)MatchState.Abandoned);
        Assert.Equal(5, (int)MatchState.NoContest);
    }

    [Fact]
    public void Quesshi_domain_csproj_gains_no_new_package_reference()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Quesshi.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var csproj = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Quesshi.Domain", "Quesshi.Domain.csproj"));
        Assert.DoesNotContain("PackageReference", csproj);
    }

    // ---- Create ----

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(11)]
    public void Create_rejects_a_length_nobody_can_choose(int count)
        => Assert.Throws<ArgumentException>(() =>
            LiveMatch.Create("lm", Challenger, [.. Enumerable.Range(0, count).Select(i => $"q{i}")], T0));

    [Fact]
    public void Create_rejects_duplicate_question_ids()
    {
        var ids = Ten.ToList();
        ids[1] = ids[0];
        Assert.Throws<ArgumentException>(() => LiveMatch.Create("lm", Challenger, ids, T0));
    }

    // ---- Rounds and the clock ----

    [Fact]
    public void A_new_duel_waits_in_the_lobby()
    {
        var m = NewMatch();
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
        Assert.Equal(LivePhase.Lobby, m.Phase);
    }

    [Fact]
    public void Join_starts_the_countdown()
    {
        var m = Joined();
        Assert.Equal(MatchState.InProgress, m.State);
        Assert.Equal(LivePhase.Countdown, m.Phase);
        Assert.Equal(T0 + LiveRules.StartCountdown, m.PhaseEndsAt);
    }

    [Fact]
    public void Advance_past_the_countdown_opens_round_zero()
    {
        var m = Joined();
        var at = T0 + LiveRules.StartCountdown;
        Assert.True(m.Advance(at));

        Assert.Equal(LivePhase.Question, m.Phase);
        Assert.NotNull(m.CurrentRound);
        Assert.Equal(0, m.CurrentRound!.Slot);
        Assert.Equal(at, m.CurrentRound.StartedAt);
        Assert.Equal(at + MatchRules.QuestionTime, m.PhaseEndsAt);
    }

    [Fact]
    public void Advance_past_a_questions_deadline_records_misses_and_moves_to_reveal()
    {
        var m = InRound0();
        var deadline = m.PhaseEndsAt!.Value;
        Assert.True(m.Advance(deadline));

        Assert.Equal(LivePhase.Reveal, m.Phase);
        Assert.Equal(deadline + LiveRules.RevealTime, m.PhaseEndsAt);

        var round = m.Rounds[0];
        Assert.Equal(-1, round.Answers[Challenger].ChoiceIndex);
        Assert.Equal(0, round.Answers[Challenger].Score);
        Assert.Equal(-1, round.Answers[Opponent].ChoiceIndex);
        Assert.Equal(1, m.MissStreak(Challenger));
        Assert.Equal(1, m.MissStreak(Opponent));
    }

    [Fact]
    public void Advance_past_a_reveal_opens_the_next_round()
    {
        var m = InRound0();
        m.Advance(m.PhaseEndsAt!.Value); // -> reveal
        var revealEnds = m.PhaseEndsAt!.Value;

        Assert.True(m.Advance(revealEnds));
        Assert.Equal(LivePhase.Question, m.Phase);
        Assert.Equal(1, m.CurrentRound!.Slot);
    }

    /// <summary>Plays every round of a duel to resolution, both players answering correctly throughout.</summary>
    private static LiveMatch PlayFullDuelToResolution()
    {
        var m = Joined();
        m.Advance(T0 + LiveRules.StartCountdown); // round 0 open
        for (var i = 0; i < Ten.Length; i++)
        {
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Answer(Opponent, m.CurrentRound.Slot, 0, true, roundStart); // closes round -> reveal
            m.Advance(m.PhaseEndsAt!.Value); // -> next round, or finishes on the last one
        }
        return m;
    }

    [Fact]
    public void Advance_past_the_last_reveal_finishes_the_duel_resolved()
    {
        var m = PlayFullDuelToResolution();
        Assert.Equal(MatchState.Resolved, m.State);
        Assert.Equal(LivePhase.Over, m.Phase);
    }

    [Fact]
    public void A_single_advance_call_crosses_several_phases_at_once()
    {
        var m = InRound0();
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, 0, 0, true, r0);
        m.Answer(Opponent, 0, 0, true, r0); // closes round 0 immediately -> reveal from r0

        // Nobody answers from here on. A single huge jump should cross reveal -> question1 ->
        // reveal1 -> question2 -> reveal2 -> question3, recording a miss for both players in each of
        // rounds 1 and 2, and conclude when round 3 also closes silent (mutual miss threshold).
        var revealEndsAt = m.PhaseEndsAt!.Value;
        var farFuture = revealEndsAt + (MatchRules.QuestionTime + LiveRules.RevealTime) * 3 + TimeSpan.FromDays(1);

        Assert.True(m.Advance(farFuture));
        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(4, m.Rounds.Count); // round 0 (answered) + rounds 1, 2, 3 (missed)
        Assert.All(m.Rounds.Skip(1), r =>
        {
            Assert.Equal(2, r.Answers.Count);
            Assert.All(r.Answers.Values, a => Assert.Equal(-1, a.ChoiceIndex));
        });
    }

    [Fact]
    public void Advance_returns_false_and_mutates_nothing_before_the_deadline()
    {
        var m = InRound0();
        var before = m.PhaseEndsAt!.Value - TimeSpan.FromSeconds(1);
        Assert.False(m.Advance(before));
        Assert.Equal(LivePhase.Question, m.Phase);
        Assert.Empty(m.Rounds[0].Answers);
    }

    [Fact]
    public void Advance_on_a_finished_duel_returns_false_and_mutates_nothing()
    {
        var m = PlayFullDuelToResolution();
        Assert.True(m.IsOver);

        Assert.False(m.Advance(m.EndedAt!.Value + TimeSpan.FromDays(1)));
        Assert.Equal(MatchState.Resolved, m.State);
    }

    // ---- Answering ----

    [Fact]
    public void An_answer_is_scored_the_same_regardless_of_when_the_player_joined()
    {
        var m = InRound0();
        var round = m.CurrentRound!;
        var answerAt = round.StartedAt + TimeSpan.FromSeconds(5);

        var a1 = m.Answer(Challenger, 0, choiceIndex: 1, correct: true, answerAt);
        Assert.Equal(Scoring.Score(true, answerAt - round.StartedAt, MatchRules.QuestionTime, Difficulty.Medium), a1.Score);
    }

    [Fact]
    public void The_second_answer_closes_the_round_immediately()
    {
        var m = InRound0();
        var round = m.CurrentRound!;
        var now = round.StartedAt + TimeSpan.FromSeconds(2);
        m.Answer(Challenger, 0, 0, true, now);

        now += TimeSpan.FromSeconds(3);
        m.Answer(Opponent, 0, 0, true, now);

        Assert.Equal(LivePhase.Reveal, m.Phase);
        Assert.Equal(now + LiveRules.RevealTime, m.PhaseEndsAt);
    }

    [Fact]
    public void A_second_answer_from_the_same_player_is_rejected()
    {
        var m = InRound0();
        var round = m.CurrentRound!;
        m.Answer(Challenger, 0, 0, true, round.StartedAt);
        Assert.Throws<InvalidOperationException>(() => m.Answer(Challenger, 0, 1, false, round.StartedAt));
    }

    [Fact]
    public void A_second_answer_from_the_same_player_does_not_overwrite_the_first()
    {
        var m = InRound0();
        var round = m.CurrentRound!;
        m.Answer(Challenger, 0, 0, true, round.StartedAt);
        var scoreBefore = round.Answers[Challenger].Score;
        try { m.Answer(Challenger, 0, 1, false, round.StartedAt); } catch (InvalidOperationException) { }
        Assert.Equal(scoreBefore, round.Answers[Challenger].Score);
        Assert.True(round.Answers[Challenger].Correct);
    }

    [Fact]
    public void An_answer_for_a_slot_that_is_not_the_current_round_is_rejected()
    {
        var m = InRound0();
        Assert.Throws<InvalidOperationException>(() => m.Answer(Challenger, 1, 0, true, m.CurrentRound!.StartedAt));
    }

    [Fact]
    public void An_answer_from_a_non_participant_is_rejected()
    {
        var m = InRound0();
        Assert.Throws<InvalidOperationException>(() => m.Answer("u-stranger", 0, 0, true, m.CurrentRound!.StartedAt));
    }

    [Fact]
    public void An_answer_arriving_after_the_round_closed_is_rejected()
    {
        var m = InRound0();
        var deadline = m.PhaseEndsAt!.Value;
        m.Advance(deadline); // closes the round -> reveal

        Assert.Throws<InvalidOperationException>(() => m.Answer(Challenger, 0, 0, true, deadline + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_answer_inside_the_network_grace_window_still_scores()
    {
        var m = InRound0();
        var round = m.CurrentRound!;
        var lateButGraced = round.StartedAt + MatchRules.QuestionTime + MatchRules.NetworkGrace - TimeSpan.FromMilliseconds(1);
        var answer = m.Answer(Challenger, 0, 0, true, lateButGraced);
        Assert.True(answer.Score > 0);
    }

    // ---- Abandonment and no-contest ----

    [Fact]
    public void Three_consecutive_misses_finishes_the_duel_abandoned_regardless_of_score()
    {
        var m = InRound0();

        // Both score in round 0, but the opponent — who is about to go silent — is ahead.
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Opponent, 0, 0, true, r0); // instant, correct: near-max score
        m.Answer(Challenger, 0, 0, true, r0 + TimeSpan.FromSeconds(15)); // slow, correct: low score
        Assert.True(m.Score(Opponent) > m.Score(Challenger));

        // The opponent goes silent for three rounds while the challenger keeps answering.
        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
            if (m.IsOver) break;
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Advance(m.PhaseEndsAt!.Value); // closes the round: opponent misses again
        }

        Assert.Equal(MatchState.Abandoned, m.State);
        Assert.Equal(Challenger, m.WinnerId);
        Assert.Equal(Opponent, m.AbandonedBy);
    }

    [Fact]
    public void An_answer_resets_the_miss_streak_so_two_misses_then_an_answer_does_not_abandon()
    {
        var m = InRound0();

        void MissOnce()
        {
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Advance(m.PhaseEndsAt!.Value); // opponent misses -> reveal
        }

        void OpenNextRound() => m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round

        MissOnce(); // opponent miss 1
        Assert.Equal(1, m.MissStreak(Opponent));
        OpenNextRound();

        MissOnce(); // opponent miss 2
        Assert.Equal(2, m.MissStreak(Opponent));
        OpenNextRound();

        // The opponent answers this round, resetting their streak.
        var roundStart = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
        m.Answer(Opponent, m.CurrentRound.Slot, 0, true, roundStart);
        Assert.Equal(0, m.MissStreak(Opponent));
        OpenNextRound();

        MissOnce(); // opponent miss 1 again, not 3
        Assert.Equal(1, m.MissStreak(Opponent));
        Assert.False(m.IsOver);
        OpenNextRound();

        MissOnce(); // opponent miss 2 again
        Assert.Equal(2, m.MissStreak(Opponent));
        Assert.False(m.IsOver);
    }

    [Fact]
    public void Both_players_missing_the_threshold_finishes_no_contest()
    {
        var m = InRound0();

        // Give round 0 an answer so the top-of-Advance staleness gate does not fire — this test is
        // about the mutual miss-threshold, not the stale-gap path.
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, 0, 0, true, r0);
        m.Answer(Opponent, 0, 0, true, r0); // closes round 0 -> reveal

        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
            if (m.IsOver) break;
            m.Advance(m.PhaseEndsAt!.Value); // closes it: both silent
        }

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Null(m.WinnerId);
        Assert.False(m.IsDraw);
    }

    [Fact]
    public void A_stale_gap_with_nobody_answering_finishes_no_contest()
    {
        var m = InRound0();
        var start = m.CurrentRound!.StartedAt;
        var farFuture = start + LiveRules.StaleAfter + TimeSpan.FromSeconds(1);

        Assert.True(m.Advance(farFuture));
        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Null(m.WinnerId);
    }

    [Fact]
    public void The_snapshot_exposes_each_players_abandonment_status()
    {
        var m = InRound0();

        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Advance(m.PhaseEndsAt!.Value); // closes round: opponent misses (or abandons, on the 3rd)
            if (m.IsOver) break;
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
        }

        var snapshot = m.ToSnapshot();
        Assert.Equal(MatchState.Abandoned, snapshot.State);
        Assert.Equal(Opponent, snapshot.AbandonedBy);
        Assert.Equal(Challenger, snapshot.WinnerId);
    }

    // ---- Snapshot ----

    [Fact]
    public void Snapshot_round_trips_and_behaves_identically_under_a_subsequent_advance()
    {
        var m = InRound0();
        var at = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, 0, 0, true, at + TimeSpan.FromSeconds(2));
        m.Answer(Opponent, 0, 0, false, at + TimeSpan.FromSeconds(3)); // closes round -> reveal

        Assert.Equal(LivePhase.Reveal, m.Phase);

        var snapshot = m.ToSnapshot();
        var restored = LiveMatch.FromSnapshot(snapshot);

        var advanceAt = m.PhaseEndsAt!.Value;
        var changedOriginal = m.Advance(advanceAt);
        var changedRestored = restored.Advance(advanceAt);

        Assert.Equal(changedOriginal, changedRestored);
        Assert.Equal(m.Phase, restored.Phase);
        Assert.Equal(m.PhaseEndsAt, restored.PhaseEndsAt);
        Assert.Equal(m.State, restored.State);
        Assert.Equal(m.Rounds.Count, restored.Rounds.Count);
        Assert.Equal(m.CurrentRound?.Slot, restored.CurrentRound?.Slot);
        Assert.Equal(m.Score(Challenger), restored.Score(Challenger));
        Assert.Equal(m.Score(Opponent), restored.Score(Opponent));
    }
}
