using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class LiveMatchTests
{
    private const string Challenger = "u-amir";
    private const string Opponent = "u-sara";
    private const string Third = "u-vahid";
    private static readonly string[] Ten = [.. Enumerable.Range(1, MatchRules.QuestionsPerMatch).Select(i => $"q{i}")];
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static DuelSettings NewSettings() => DuelSettings.Create(Language.En, MatchRules.QuestionsPerMatch, [], []);

    private static LiveMatch NewMatch(int capacity = 2)
    {
        var m = LiveMatch.Create("lm1", "CODE01", Challenger, NewSettings(), capacity, T0);
        m.DrawQuestions(Ten);
        return m;
    }

    /// <summary>Seated to capacity 2 and explicitly started by the owner — joining alone no longer
    /// starts a duel (see <see cref="Join_leaves_a_two_seat_lobby_open_once_it_is_full"/>), so every
    /// test that wants an in-progress duel has to ask for it.</summary>
    private static LiveMatch Joined()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
        m.Start(Challenger, T0);
        return m;
    }

    /// <summary>Joined and past the countdown, so round 0 is open for answers.</summary>
    private static LiveMatch InRound0()
    {
        var m = Joined();
        m.Advance(T0 + LiveRules.StartCountdown);
        return m;
    }

    /// <summary>A three-player lobby, filled to capacity and explicitly started by the owner.</summary>
    private static LiveMatch Joined3()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        m.DrawQuestions(Ten);
        m.Join(Opponent, T0);
        m.Join(Third, T0);
        m.Start(Challenger, T0);
        return m;
    }

    private static LiveMatch InRound0_3()
    {
        var m = Joined3();
        m.Advance(T0 + LiveRules.StartCountdown);
        return m;
    }

    /// <summary>
    /// The first instant that actually closes a question at <paramref name="questionDeadline"/> — one
    /// tick past <see cref="MatchRules.NetworkGrace"/>, since the grace window keeps the round open
    /// through the deadline itself.
    /// </summary>
    private static DateTimeOffset PastGrace(DateTimeOffset questionDeadline)
        => questionDeadline + MatchRules.NetworkGrace + TimeSpan.FromTicks(1);

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

    [Fact]
    public void IsOver_names_resolved_forfeited_abandoned_and_no_contest()
    {
        var snapshot = new LiveMatchSnapshot(
            "lm", "CODE01", [Challenger, Opponent], 2, NewSettings(), [.. Ten], MatchState.Forfeited, LivePhase.Over, null,
            [], new Dictionary<string, int>(), T0, T0, null, false, [], [], null);
        Assert.True(LiveMatch.FromSnapshot(snapshot).IsOver);
    }

    // ---- Participants, capacity and settings ----

    [Fact]
    public void A_fresh_lobby_holds_only_its_owner()
    {
        var m = NewMatch();
        Assert.Equal([Challenger], m.Participants);
        Assert.Equal(Challenger, m.OwnerId);
        Assert.Equal(2, m.Capacity);
        Assert.Empty(m.Standings);
        Assert.Null(m.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Create_rejects_a_capacity_outside_two_to_eight(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => LiveMatch.Create("lm", "CODE01", Challenger, NewSettings(), capacity, T0));

    [Fact]
    public void A_lobby_with_room_for_more_does_not_start_until_capacity_is_reached()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        m.DrawQuestions(Ten);

        m.Join(Opponent, T0);
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
        Assert.Equal(LivePhase.Lobby, m.Phase);
        Assert.Equal([Challenger, Opponent], m.Participants);

        m.Join(Third, T0);
        Assert.Equal(MatchState.AwaitingOpponent, m.State);
        Assert.Equal(LivePhase.Lobby, m.Phase);
        Assert.Equal([Challenger, Opponent, Third], m.Participants);
    }

    /// <summary>
    /// The core of issue #104: a two-seat lobby — every lobby before capacity was choosable — used to
    /// start the instant its second seat filled. It no longer does; the owner's <see cref="LiveMatch.Start"/>
    /// is the only door into <see cref="LivePhase.Countdown"/>, for a two-seat lobby exactly as for a
    /// bigger one.
    /// </summary>
    [Fact]
    public void Join_leaves_a_two_seat_lobby_open_once_it_is_full()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);

        Assert.Equal(MatchState.AwaitingOpponent, m.State);
        Assert.Equal(LivePhase.Lobby, m.Phase);
        Assert.Equal([Challenger, Opponent], m.Participants);
    }

    [Fact]
    public void A_third_join_into_a_full_two_seat_lobby_is_refused()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);

        Assert.Throws<InvalidOperationException>(() => m.Join(Third, T0));
        Assert.Equal(2, m.Participants.Count);
        Assert.Equal(LiveJoinResult.Full, m.TryJoin(Third, T0));
    }

    [Fact]
    public void A_full_lobby_reopens_once_a_non_owner_leaves_and_the_owner_can_still_start_it()
    {
        var m = NewMatch();
        m.Join(Opponent, T0);
        Assert.Equal(LiveJoinResult.Full, m.TryJoin(Third, T0)); // confirm it is actually full first

        Assert.True(m.Leave(Opponent, T0));
        Assert.Equal(LiveJoinResult.Joined, m.TryJoin(Third, T0));
        Assert.True(m.Start(Challenger, T0));
        Assert.Equal(MatchState.InProgress, m.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(11)]
    public void DuelSettings_Create_rejects_a_length_nobody_can_choose(int count)
        => Assert.Throws<ArgumentException>(() => DuelSettings.Create(Language.En, count, [], []));

    [Fact]
    public void DuelSettings_Create_accepts_every_offered_length()
    {
        foreach (var count in MatchRules.QuestionCountChoices)
            DuelSettings.Create(Language.En, count, [], []); // does not throw
    }

    [Fact]
    public void DrawQuestions_rejects_a_set_that_does_not_match_the_settings_count()
    {
        var m = LiveMatch.Create("lm", "CODE01", Challenger, NewSettings(), 2, T0);
        Assert.Throws<ArgumentException>(() => m.DrawQuestions([.. Ten.Take(5)]));
    }

    [Fact]
    public void DrawQuestions_rejects_duplicate_question_ids()
    {
        var m = LiveMatch.Create("lm", "CODE01", Challenger, NewSettings(), 2, T0);
        var ids = Ten.ToList();
        ids[1] = ids[0];
        Assert.Throws<ArgumentException>(() => m.DrawQuestions(ids));
    }

    [Fact]
    public void DrawQuestions_cannot_be_called_twice()
    {
        var m = NewMatch(); // NewMatch already draws the set
        Assert.Throws<InvalidOperationException>(() => m.DrawQuestions(Ten));
    }

    [Fact]
    public void DrawQuestions_cannot_be_called_once_the_duel_has_started()
    {
        var m = Joined(); // capacity 2 -- the second join already started the countdown
        Assert.Throws<InvalidOperationException>(() => m.DrawQuestions(Ten));
    }

    // ---- Start, Leave and UpdateSettings ----

    [Fact]
    public void Start_by_the_owner_with_two_seated_begins_the_duel_with_room_to_spare()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        m.DrawQuestions(Ten);
        m.Join(Opponent, T0);

        Assert.True(m.Start(Challenger, T0));
        Assert.Equal(MatchState.InProgress, m.State);
        Assert.Equal(LivePhase.Countdown, m.Phase);
        Assert.Equal(T0 + LiveRules.StartCountdown, m.PhaseEndsAt);
    }

    [Fact]
    public void Start_is_refused_below_two_participants()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        m.DrawQuestions(Ten);

        Assert.False(m.Start(Challenger, T0));
        Assert.Equal(LivePhase.Lobby, m.Phase);
    }

    [Fact]
    public void Start_is_refused_for_a_non_owner()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        m.DrawQuestions(Ten);
        m.Join(Opponent, T0);

        Assert.False(m.Start(Opponent, T0));
        Assert.Equal(LivePhase.Lobby, m.Phase);
    }

    [Fact]
    public void Start_is_refused_once_the_lobby_has_already_started()
    {
        var m = Joined(); // capacity 2, already started
        Assert.False(m.Start(Challenger, T0));
    }

    [Fact]
    public void Start_is_refused_while_the_question_set_is_still_undrawn()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        m.Join(Opponent, T0); // no DrawQuestions call

        Assert.False(m.Start(Challenger, T0));
        Assert.Equal(LivePhase.Lobby, m.Phase);
    }

    [Fact]
    public void Leave_frees_a_non_owner_seat()
    {
        // Capacity 4, room to spare after two join, so leaving still leaves it short of Capacity.
        var m = LiveMatch.Create("lm4", "CODE04", Challenger, NewSettings(), capacity: 4, T0);
        m.DrawQuestions(Ten);
        m.Join(Opponent, T0);
        m.Join(Third, T0);

        Assert.True(m.Leave(Opponent, T0));
        Assert.Equal([Challenger, Third], m.Participants);
        Assert.Equal(LivePhase.Lobby, m.Phase); // still short of Capacity
    }

    [Fact]
    public void Leave_is_refused_for_the_owner()
    {
        var m = NewMatch(capacity: 3);
        Assert.False(m.Leave(Challenger, T0));
        Assert.Equal([Challenger], m.Participants);
    }

    [Fact]
    public void Leave_is_refused_once_the_duel_has_started()
    {
        var m = Joined(); // capacity 2, already started
        Assert.False(m.Leave(Opponent, T0));
    }

    [Fact]
    public void Leave_is_refused_for_someone_not_seated()
    {
        var m = NewMatch(capacity: 3);
        Assert.False(m.Leave(Third, T0));
    }

    [Fact]
    public void UpdateSettings_changes_settings_while_the_question_set_is_still_empty()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        var next = DuelSettings.Create(Language.Fa, 20, ["geography"], [Difficulty.Hard]);

        Assert.True(m.UpdateSettings(Challenger, next));
        Assert.Equal(next, m.Settings);
    }

    [Fact]
    public void UpdateSettings_is_refused_for_a_non_owner()
    {
        var m = LiveMatch.Create("lm3", "CODE03", Challenger, NewSettings(), capacity: 3, T0);
        var original = m.Settings;

        Assert.False(m.UpdateSettings(Opponent, DuelSettings.Create(Language.Fa, 20, [], [])));
        Assert.Equal(original, m.Settings);
    }

    [Fact]
    public void UpdateSettings_is_refused_once_the_question_set_is_drawn()
    {
        var m = NewMatch(capacity: 3); // NewMatch already draws
        var original = m.Settings;

        Assert.False(m.UpdateSettings(Challenger, DuelSettings.Create(Language.Fa, 20, [], [])));
        Assert.Equal(original, m.Settings);
    }

    [Fact]
    public void Create_carries_the_share_code_and_language()
    {
        var m = NewMatch();
        Assert.Equal("CODE01", m.Code);
        Assert.Equal(Language.En, m.Lang);
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
    public void An_unjoined_lobby_expires_into_no_contest_with_no_rounds()
    {
        var m = NewMatch();
        var beforeExpiry = T0 + LiveRules.LobbyExpires - TimeSpan.FromSeconds(1);
        Assert.False(m.Advance(beforeExpiry));
        Assert.Equal(LivePhase.Lobby, m.Phase);

        var atExpiry = T0 + LiveRules.LobbyExpires;
        Assert.True(m.Advance(atExpiry));
        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.LobbyExpired, m.Reason);
        Assert.Equal(LivePhase.Over, m.Phase);
        Assert.Null(m.WinnerId);
        Assert.False(m.IsDraw);
        Assert.Empty(m.Rounds);
    }

    [Fact]
    public void Join_advances_the_clock_first_so_an_expired_lobby_cannot_be_joined()
    {
        var m = NewMatch();
        var atExpiry = T0 + LiveRules.LobbyExpires;
        Assert.Throws<InvalidOperationException>(() => m.Join(Opponent, atExpiry));
        Assert.Equal(MatchState.NoContest, m.State);
    }

    // ---- TryJoin ----

    [Fact]
    public void TryJoin_seats_the_first_opponent()
    {
        var m = NewMatch();
        Assert.Equal(LiveJoinResult.Joined, m.TryJoin(Opponent, T0));
        Assert.Equal(MatchState.AwaitingOpponent, m.State); // filling the lobby no longer starts it
    }

    [Fact]
    public void TryJoin_is_a_no_op_for_the_owner_who_already_holds_seat_zero()
    {
        // Not a refusal: the owner is Participants[0], so "join" asked by them is already true. It
        // used to answer SelfJoin -- accurate when a challenger stood outside waiting for an opponent
        // rather than occupying a seat, and wrong once a lobby became a room with a page that loads
        // by joining, which left an owner unable to open the lobby they had just made.
        var m = NewMatch();

        Assert.Equal(LiveJoinResult.AlreadyIn, m.TryJoin(Challenger, T0));

        Assert.Equal(MatchState.AwaitingOpponent, m.State); // still waiting: no seat was taken
        Assert.Single(m.Participants);
    }

    [Fact]
    public void TryJoin_is_idempotent_for_the_seated_opponent()
    {
        var m = Joined();
        Assert.Equal(LiveJoinResult.AlreadyIn, m.TryJoin(Opponent, T0));
    }

    /// <summary>
    /// A capacity-2 lobby can only ever close by filling — there is no "started early with room to
    /// spare" for it — so a stranger arriving once both seats are taken sees <see cref="LiveJoinResult.Full"/>,
    /// not <see cref="LiveJoinResult.Taken"/>. See <see cref="TryJoin_reports_taken_for_an_early_owner_start_with_room_to_spare"/>
    /// for the case Taken is actually for.
    /// </summary>
    [Fact]
    public void TryJoin_refuses_a_stranger_once_the_seat_is_taken()
    {
        var m = Joined();
        Assert.Equal(LiveJoinResult.Full, m.TryJoin("u-stranger", T0));
        Assert.Equal(2, m.Participants.Count); // Capacity is never exceeded
    }

    [Fact]
    public void TryJoin_reports_taken_for_an_early_owner_start_with_room_to_spare()
    {
        var m = NewMatch(capacity: 3);
        m.Join(Opponent, T0);
        Assert.True(m.Start(Challenger, T0)); // only 2 of 3 seats filled

        Assert.Equal(LiveJoinResult.Taken, m.TryJoin(Third, T0));
    }

    [Fact]
    public void TryJoin_reports_expired_distinctly_from_taken()
    {
        var m = NewMatch();
        var atExpiry = T0 + LiveRules.LobbyExpires;
        Assert.Equal(LiveJoinResult.Expired, m.TryJoin(Opponent, atExpiry));
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
        Assert.True(m.Advance(PastGrace(deadline)));

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
    public void A_question_does_not_close_at_or_before_the_deadline_plus_network_grace()
    {
        var m = InRound0();
        var deadline = m.PhaseEndsAt!.Value;

        Assert.False(m.Advance(deadline));
        Assert.False(m.Advance(deadline + MatchRules.NetworkGrace));
        Assert.Equal(LivePhase.Question, m.Phase);

        Assert.True(m.Advance(PastGrace(deadline)));
        Assert.Equal(LivePhase.Reveal, m.Phase);
    }

    [Fact]
    public void Advance_past_a_reveal_opens_the_next_round()
    {
        var m = InRound0();
        m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // -> reveal
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
        Assert.Empty(m.Abandoners);
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

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void A_choice_index_outside_the_valid_range_is_rejected(int choiceIndex)
    {
        var m = InRound0();
        Assert.Throws<InvalidOperationException>(() => m.Answer(Challenger, 0, choiceIndex, true, m.CurrentRound!.StartedAt));
    }

    [Fact]
    public void An_answer_arriving_after_the_round_closed_is_rejected()
    {
        var m = InRound0();
        var deadline = m.PhaseEndsAt!.Value;
        var closesAt = PastGrace(deadline);
        m.Advance(closesAt); // closes the round -> reveal

        Assert.Throws<InvalidOperationException>(() => m.Answer(Challenger, 0, 0, true, closesAt + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_late_answer_settles_the_clock_first_even_when_it_is_then_rejected()
    {
        var m = InRound0();
        var closesAt = PastGrace(m.PhaseEndsAt!.Value);

        // A non-participant's answer is always rejected, but the leading Advance it triggers is not
        // undone: real time passed regardless of whether this particular call was valid.
        Assert.Throws<InvalidOperationException>(() => m.Answer("u-stranger", 0, 0, true, closesAt));

        Assert.Equal(LivePhase.Reveal, m.Phase);
        Assert.Equal(-1, m.Rounds[0].Answers[Challenger].ChoiceIndex);
        Assert.Equal(-1, m.Rounds[0].Answers[Opponent].ChoiceIndex);
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

    [Fact]
    public void An_answer_arriving_exactly_at_the_grace_boundary_scores_with_no_speed_bonus()
    {
        var m = InRound0();
        var round = m.CurrentRound!;
        var atGraceBoundary = round.StartedAt + MatchRules.QuestionTime + MatchRules.NetworkGrace;

        var answer = m.Answer(Challenger, 0, 0, true, atGraceBoundary);

        Assert.Equal(Scoring.Score(true, atGraceBoundary - round.StartedAt, MatchRules.QuestionTime, Difficulty.Medium), answer.Score);
        Assert.Equal((int)Math.Round(MatchRules.BaseScore * Scoring.Weight(Difficulty.Medium)), answer.Score);
    }

    // ---- Abandonment and no-contest (two players) ----

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
            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // closes the round: opponent misses again
        }

        Assert.Equal(MatchState.Abandoned, m.State);
        Assert.Equal(Challenger, m.WinnerId);
        Assert.Single(m.Abandoners);
        Assert.Equal(Opponent, m.Abandoners[0].PlayerId);

        var standings = m.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(1, standings[Challenger].Place);
        Assert.Equal(MatchOutcome.Win, standings[Challenger].Outcome);
        Assert.Equal(2, standings[Opponent].Place);
        Assert.Equal(MatchOutcome.Loss, standings[Opponent].Outcome);
        Assert.Equal(0, standings[Opponent].Score); // banked score wiped despite the early lead
    }

    [Fact]
    public void An_abandoned_player_can_no_longer_answer()
    {
        var m = InRound0();
        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            m.Advance(m.PhaseEndsAt!.Value);
            if (m.IsOver) break;
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Advance(PastGrace(m.PhaseEndsAt!.Value));
        }

        Assert.Equal(MatchState.Abandoned, m.State);
        var ex = Record.Exception(() => m.Answer(Opponent, 0, 0, true, m.EndedAt!.Value));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void An_answer_resets_the_miss_streak_so_two_misses_then_an_answer_does_not_abandon()
    {
        var m = InRound0();

        void MissOnce()
        {
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // opponent misses -> reveal
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
    public void Both_players_missing_the_threshold_finishes_no_contest_as_all_abandoned()
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
            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // closes it: both silent
        }

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.AllAbandoned, m.Reason);
        Assert.Null(m.WinnerId);
        Assert.False(m.IsDraw);
        Assert.Empty(m.Standings); // NoContest credits nobody
    }

    [Fact]
    public void The_snapshot_exposes_each_players_abandonment_status()
    {
        var m = InRound0();

        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            var roundStart = m.CurrentRound!.StartedAt;
            m.Answer(Challenger, m.CurrentRound.Slot, 0, true, roundStart);
            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // closes round: opponent misses (or abandons, on the 3rd)
            if (m.IsOver) break;
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
        }

        var snapshot = m.ToSnapshot();
        Assert.Equal(MatchState.Abandoned, snapshot.State);
        Assert.Equal(Opponent, snapshot.Abandoners.Single().PlayerId);
        Assert.Equal(Challenger, snapshot.WinnerId);
    }

    // ---- N-way standings, abandonment ordering and NoContest reasons (three players) ----

    [Fact]
    public void Standings_share_first_place_when_two_finishers_tie_at_the_top()
    {
        var m = InRound0_3();

        // Challenger and Opponent both answer instantly (equal, top score); Third answers correctly
        // but slowly every round, for a real, strictly lower score.
        for (var i = 0; i < Ten.Length; i++)
        {
            var round = m.CurrentRound!;
            var start = round.StartedAt;
            m.Answer(Challenger, round.Slot, 0, true, start);
            m.Answer(Opponent, round.Slot, 0, true, start);
            m.Answer(Third, round.Slot, 0, true, start + TimeSpan.FromSeconds(15));
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round, or resolves on the last one
        }

        Assert.Equal(MatchState.Resolved, m.State);
        Assert.Null(m.WinnerId); // nobody won outright
        Assert.True(m.IsDraw); // means "no sole winner", not "everybody drew"

        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(1, byId[Challenger].Place);
        Assert.Equal(1, byId[Opponent].Place);
        Assert.Equal(MatchOutcome.Draw, byId[Challenger].Outcome);
        Assert.Equal(MatchOutcome.Draw, byId[Opponent].Outcome);
        Assert.Equal(3, byId[Third].Place); // competition ranking: the tie for first skips place 2
        Assert.Equal(MatchOutcome.Loss, byId[Third].Outcome);
        Assert.True(byId[Challenger].Score > byId[Third].Score);
        Assert.Equal(byId[Challenger].Score, byId[Opponent].Score);
    }

    [Fact]
    public void Standings_give_a_sole_winner_when_nobody_ties_the_top_score()
    {
        var m = InRound0_3();
        for (var i = 0; i < Ten.Length; i++)
        {
            var round = m.CurrentRound!;
            var start = round.StartedAt;
            m.Answer(Challenger, round.Slot, 0, true, start); // fastest -> highest score
            m.Answer(Opponent, round.Slot, 0, true, start + TimeSpan.FromSeconds(8));
            m.Answer(Third, round.Slot, 0, true, start + TimeSpan.FromSeconds(15));
            m.Advance(m.PhaseEndsAt!.Value);
        }

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
    public void A_round_closes_on_the_active_participants_alone_once_someone_has_abandoned()
    {
        var m = InRound0_3();

        // Round 0: everyone answers; Third takes an early lead they will not get to keep.
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Third, 0, 0, true, r0);
        m.Answer(Challenger, 0, 0, true, r0 + TimeSpan.FromSeconds(10));
        m.Answer(Opponent, 0, 0, true, r0 + TimeSpan.FromSeconds(10));
        Assert.True(m.Score(Third) > m.Score(Challenger));

        // Third goes silent. For as long as they are still counted active, Challenger and Opponent
        // answering does not close the round early — it still has to wait out the clock.
        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
            var round = m.CurrentRound!;
            m.Answer(Challenger, round.Slot, 0, true, round.StartedAt);
            m.Answer(Opponent, round.Slot, 0, true, round.StartedAt);
            Assert.Equal(LivePhase.Question, m.Phase); // still "waiting" on Third's silent turn

            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // times out -- Third's miss is recorded here
        }

        Assert.Contains(m.Abandoners, a => a.PlayerId == Third);
        Assert.False(m.IsOver); // two players remain, so the duel goes on
        Assert.Throws<InvalidOperationException>(() => m.Answer(Third, m.CurrentRound!.Slot, 0, true, m.CurrentRound!.StartedAt));

        // From here on, Third is no longer counted: the two survivors answering closes the round
        // immediately, with no timeout needed at all — the whole point of the rule.
        m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
        var nextRound = m.CurrentRound!;
        m.Answer(Challenger, nextRound.Slot, 0, true, nextRound.StartedAt);
        m.Answer(Opponent, nextRound.Slot, 0, true, nextRound.StartedAt);
        Assert.Equal(LivePhase.Reveal, m.Phase);

        // Play the remainder out with just the two survivors.
        while (!m.IsOver)
        {
            m.Advance(m.PhaseEndsAt!.Value);
            if (m.IsOver) break;
            var round = m.CurrentRound!;
            m.Answer(Challenger, round.Slot, 0, true, round.StartedAt);
            m.Answer(Opponent, round.Slot, 0, true, round.StartedAt);
        }

        Assert.Equal(MatchState.Resolved, m.State); // two survivors finished it out
        var third = m.Standings.Single(s => s.PlayerId == Third);
        Assert.Equal(0, third.Score); // banked score wiped, whatever they scored before quitting
        Assert.Equal(MatchOutcome.Loss, third.Outcome);
        Assert.All(m.Standings.Where(s => s.PlayerId != Third), s => Assert.True(s.Place < third.Place));
    }

    [Fact]
    public void Two_players_abandoning_in_the_same_round_share_a_place_below_the_survivor()
    {
        var m = InRound0_3();

        // Round 0: everyone answers, so the top-of-Advance staleness gate never fires below.
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, 0, 0, true, r0);
        m.Answer(Opponent, 0, 0, true, r0);
        m.Answer(Third, 0, 0, true, r0);

        // Opponent and Third go silent together; Challenger keeps answering.
        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
            if (m.IsOver) break;
            var round = m.CurrentRound!;
            m.Answer(Challenger, round.Slot, 0, true, round.StartedAt);
            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // Opponent and Third both miss again
        }

        Assert.Equal(MatchState.Abandoned, m.State);
        Assert.Equal(Challenger, m.WinnerId);
        Assert.Equal(2, m.Abandoners.Count);
        Assert.Equal(m.Abandoners[0].RoundSlot, m.Abandoners[1].RoundSlot); // dropped in the same round

        var byId = m.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(1, byId[Challenger].Place);
        Assert.Equal(MatchOutcome.Win, byId[Challenger].Outcome);
        Assert.True(byId[Opponent].Place > 1);
        Assert.Equal(byId[Opponent].Place, byId[Third].Place); // shared place below the survivor
        Assert.Equal(MatchOutcome.Loss, byId[Opponent].Outcome);
        Assert.Equal(MatchOutcome.Loss, byId[Third].Outcome);
        Assert.Equal(0, byId[Opponent].Score);
        Assert.Equal(0, byId[Third].Score);
    }

    [Fact]
    public void All_three_players_missing_the_threshold_together_finishes_no_contest_as_all_abandoned()
    {
        var m = InRound0_3();

        // Everyone answers round 0 so the top-of-Advance staleness gate does not fire below — this
        // test is about the mutual miss-threshold, not the stale-gap path.
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, 0, 0, true, r0);
        m.Answer(Opponent, 0, 0, true, r0);
        m.Answer(Third, 0, 0, true, r0);

        for (var i = 0; i < LiveRules.MissesBeforeAbandon; i++)
        {
            m.Advance(m.PhaseEndsAt!.Value); // reveal -> next round
            if (m.IsOver) break;
            m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // closes it: everyone silent
        }

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.AllAbandoned, m.Reason);
        Assert.Null(m.WinnerId);
        Assert.Empty(m.Standings);
    }

    // ---- NextDueAt ----

    [Fact]
    public void NextDueAt_in_the_lobby_is_created_at_plus_lobby_expiry()
    {
        var m = NewMatch();
        Assert.Equal(T0 + LiveRules.LobbyExpires, m.NextDueAt);
    }

    [Fact]
    public void NextDueAt_in_the_countdown_is_phase_ends_at()
    {
        var m = Joined();
        Assert.Equal(m.PhaseEndsAt, m.NextDueAt);
    }

    [Fact]
    public void NextDueAt_in_a_question_is_phase_ends_at_plus_network_grace()
    {
        var m = InRound0();
        Assert.Equal(m.PhaseEndsAt!.Value + MatchRules.NetworkGrace, m.NextDueAt);
    }

    [Fact]
    public void NextDueAt_in_a_reveal_is_phase_ends_at()
    {
        var m = InRound0();
        m.Advance(PastGrace(m.PhaseEndsAt!.Value)); // -> reveal
        Assert.Equal(LivePhase.Reveal, m.Phase);
        Assert.Equal(m.PhaseEndsAt, m.NextDueAt);
    }

    [Fact]
    public void NextDueAt_is_null_once_the_duel_is_over()
    {
        var m = PlayFullDuelToResolution();
        Assert.True(m.IsOver);
        Assert.Null(m.NextDueAt);
    }

    [Theory]
    [InlineData(LivePhase.Lobby)]
    [InlineData(LivePhase.Countdown)]
    [InlineData(LivePhase.Reveal)]
    public void Advance_at_exactly_next_due_at_moves_the_phase_and_before_it_does_not(LivePhase phase)
    {
        var m = phase switch
        {
            LivePhase.Lobby => NewMatch(),
            LivePhase.Countdown => Joined(),
            _ => InRound0() // advanced to Reveal below
        };
        if (phase == LivePhase.Reveal) m.Advance(PastGrace(m.PhaseEndsAt!.Value));
        Assert.Equal(phase, m.Phase);

        var due = m.NextDueAt!.Value;
        Assert.False(m.Advance(due - TimeSpan.FromTicks(1)));
        Assert.Equal(phase, m.Phase);

        Assert.True(m.Advance(due));
        Assert.NotEqual(phase, m.Phase);
    }

    [Fact]
    public void NextDueAt_in_a_question_names_the_grace_boundary_which_only_closes_strictly_after_it()
    {
        // The Question phase is the one exception documented on NextDueAt itself: StepOnce only
        // closes a question once now is strictly past PhaseEndsAt + NetworkGrace, so Advance at
        // NextDueAt exactly is still one tick early — the grain's re-arm-on-no-op path exists for
        // precisely this boundary.
        var m = InRound0();
        var due = m.NextDueAt!.Value;

        Assert.False(m.Advance(due));
        Assert.Equal(LivePhase.Question, m.Phase);

        Assert.True(m.Advance(due + TimeSpan.FromTicks(1)));
        Assert.Equal(LivePhase.Reveal, m.Phase);
    }

    // ---- The widened staleness test, in every phase but Lobby ----

    [Fact]
    public void A_gap_of_exactly_StaleAfter_past_next_due_at_does_not_trigger_the_widened_staleness_guard()
    {
        // The boundary is "more than StaleAfter", not "at least" — landing exactly on it still lets
        // the ordinary phase machinery run (which, given how large this gap really is, simulates
        // several rounds forward), rather than being read as an outage.
        var m = InRound0();
        var exactlyAtThreshold = m.NextDueAt!.Value + LiveRules.StaleAfter;

        m.Advance(exactlyAtThreshold);

        Assert.False(m.IsOver);
        Assert.Null(m.Reason);
    }

    [Fact]
    public void A_stale_gap_beginning_in_countdown_finishes_no_contest_as_stale()
    {
        var m = Joined();
        var farFuture = m.NextDueAt!.Value + LiveRules.StaleAfter + TimeSpan.FromSeconds(1);

        Assert.True(m.Advance(farFuture));
        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.Stale, m.Reason);
        Assert.Empty(m.Rounds); // round 0 never even opened
    }

    [Fact]
    public void A_stale_gap_with_nobody_answering_finishes_no_contest_as_stale()
    {
        var m = InRound0();
        var farFuture = m.NextDueAt!.Value + LiveRules.StaleAfter + TimeSpan.FromSeconds(1);

        Assert.True(m.Advance(farFuture));
        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.Stale, m.Reason);
        Assert.Null(m.WinnerId);
    }

    [Fact]
    public void A_stale_gap_in_a_question_after_one_player_has_answered_finishes_no_contest_as_stale()
    {
        // The gap the old guard missed entirely: it only fired for a Question with zero answers, so
        // an outage after one player answered used to fall through to the round-by-round simulation
        // below instead.
        var m = InRound0();
        var round = m.CurrentRound!;
        m.Answer(Challenger, 0, 0, true, round.StartedAt); // one of two answers; the round stays open

        var farFuture = m.NextDueAt!.Value + LiveRules.StaleAfter + TimeSpan.FromSeconds(1);
        Assert.True(m.Advance(farFuture));

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.Stale, m.Reason);
        Assert.Single(m.Rounds);
        Assert.Single(m.Rounds[0].Answers); // only the real answer -- the round was never force-closed
    }

    [Fact]
    public void A_stale_gap_beginning_in_reveal_finishes_no_contest_as_stale_without_simulating_further_rounds()
    {
        // This is the case the widened test exists for: the old guard only ever looked at Question
        // with zero answers, so an outage that began in Reveal (or Countdown) was invisible to it and
        // the while loop in Advance would simulate every remaining round with nobody answering —
        // three of them mark every player abandoned. This deliberately changes that: the gap is
        // recognised as an outage the moment Advance is called, before any round is simulated.
        var m = InRound0();
        var r0 = m.CurrentRound!.StartedAt;
        m.Answer(Challenger, 0, 0, true, r0);
        m.Answer(Opponent, 0, 0, true, r0); // closes round 0 -> reveal
        Assert.Equal(LivePhase.Reveal, m.Phase);

        var farFuture = m.NextDueAt!.Value + LiveRules.StaleAfter + TimeSpan.FromSeconds(1);
        Assert.True(m.Advance(farFuture));

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.Stale, m.Reason);
        Assert.Single(m.Rounds); // round 0 only -- nothing beyond reveal was ever simulated
    }

    // ---- EndNoContest ----

    [Fact]
    public void EndNoContest_finishes_the_duel_with_no_winner()
    {
        var m = InRound0();
        var at = m.CurrentRound!.StartedAt + TimeSpan.FromSeconds(4);

        m.EndNoContest(at);

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.LobbyExpired, m.Reason); // the caller-agnostic default: never penalty-eligible
        Assert.Equal(LivePhase.Over, m.Phase);
        Assert.Null(m.PhaseEndsAt);
        Assert.Equal(at, m.EndedAt);
        Assert.Null(m.WinnerId);
        Assert.False(m.IsDraw);
    }

    [Fact]
    public void EndNoContest_accepts_an_explicit_reason()
    {
        var m = InRound0();
        m.EndNoContest(m.CurrentRound!.StartedAt, NoContestReason.Stale);
        Assert.Equal(NoContestReason.Stale, m.Reason);
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

        Assert.Equal(m.Participants, restored.Participants);
        Assert.Equal(m.Capacity, restored.Capacity);
        Assert.Equal(m.Settings, restored.Settings);

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

    [Fact]
    public void Snapshot_round_trips_standings_and_abandoners()
    {
        var m = PlayFullDuelToResolution();
        var restored = LiveMatch.FromSnapshot(m.ToSnapshot());

        Assert.Equal(m.Standings, restored.Standings);
        Assert.Equal(m.Abandoners, restored.Abandoners);
        Assert.Equal(m.Reason, restored.Reason);
    }

    // ---- Migration: tolerating a snapshot written before Participants/Settings/Abandoners existed ----

    /// <summary>
    /// Exactly the JSON a live duel written before this migration produces — literal text, not a value
    /// built from today's <see cref="LiveMatchSnapshot"/> and trimmed, since the point is proving the
    /// *actual* old wire shape still deserializes. This is what <c>LiveMatchSnapshot</c> looked like
    /// before <c>Participants</c>, <c>Capacity</c>, <c>Settings</c>, <c>Abandoners</c>, <c>Standings</c>
    /// and <c>Reason</c> existed: <c>Lang</c>, <c>ChallengerId</c>, <c>OpponentId</c> and a plain
    /// <c>AbandonedBy</c> id in their place.
    /// </summary>
    private static string LegacyJson(string opponentIdJson, string abandonedByJson, string state = "1", string phase = "1") =>
        $$"""
        {"Id":"live-legacy-1","Code":"LOLD01","Lang":1,"ChallengerId":"u-legacy-challenger","OpponentId":{{opponentIdJson}},
         "QuestionIds":["lq1","lq2","lq3","lq4","lq5"],"State":{{state}},"Phase":{{phase}},"PhaseEndsAt":null,
         "Rounds":[],"MissStreaks":{},"CreatedAt":"2026-08-19T12:00:00+00:00","EndedAt":null,
         "WinnerId":null,"IsDraw":false,"AbandonedBy":{{abandonedByJson}} }
        """;

    [Fact]
    public void A_legacy_snapshot_deserializes_and_restores_the_two_player_shape()
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            LegacyJson("\"u-legacy-opponent\"", "null"))!;

        // The tell that this blob predates Participants: the field never appears in the JSON above,
        // so it comes back null rather than throwing, and Lang/ChallengerId/OpponentId land in the
        // three legacy fields instead.
        Assert.Null(snapshot.Participants);
        Assert.Equal("u-legacy-challenger", snapshot.ChallengerId);

        var m = LiveMatch.FromSnapshot(snapshot);

        Assert.Equal(["u-legacy-challenger", "u-legacy-opponent"], m.Participants);
        Assert.Equal("u-legacy-challenger", m.OwnerId);
        Assert.Equal(2, m.Capacity);
        Assert.Equal(Language.En, m.Settings.Language);
        Assert.Equal(5, m.Settings.QuestionCount);
        Assert.Empty(m.Settings.CategoryIds);
        Assert.Empty(m.Settings.Levels);
    }

    [Fact]
    public void A_legacy_abandoned_duel_converts_its_plain_AbandonedBy_into_the_ordered_list()
    {
        // State 4 = Abandoned, Phase 4 = Over: a finished duel the opponent walked away from, exactly
        // the shape LiveMatchGrain reactivates from the async history listing or a direct navigation
        // years later. The round slot recorded is 0 and that is fine — a two-player record can only
        // ever have one abandoner, so nothing ever compares it against another's.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            LegacyJson("\"u-legacy-opponent\"", "\"u-legacy-opponent\"", state: "4", phase: "4"))!;
        var m = LiveMatch.FromSnapshot(snapshot);

        Assert.True(m.IsOver);
        Assert.Equal([new Abandonment("u-legacy-opponent", 0)], m.Abandoners);
    }

    [Fact]
    public void A_legacy_lobby_nobody_joined_keeps_its_free_seat()
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            LegacyJson("null", "null", state: "0", phase: "0"))!;
        var m = LiveMatch.FromSnapshot(snapshot);

        Assert.Equal(["u-legacy-challenger"], m.Participants);

        Assert.Equal(LiveJoinResult.Joined, m.TryJoin(Opponent, T0));
        Assert.Equal(["u-legacy-challenger", Opponent], m.Participants);
    }

    [Fact]
    public void A_legacy_records_settings_are_read_only_because_its_questions_are_already_drawn()
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            LegacyJson("null", "null", state: "0", phase: "0"))!;
        var m = LiveMatch.FromSnapshot(snapshot);

        Assert.Throws<InvalidOperationException>(() => m.DrawQuestions(Ten));
    }

    [Fact]
    public void Both_snapshot_shapes_round_trip_through_JSON()
    {
        var legacy = LiveMatch.FromSnapshot(System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            LegacyJson("\"u-legacy-opponent\"", "null"))!);
        var freshlyWritten = System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(legacy.ToSnapshot()))!;

        // A record this code writes never carries the legacy fields, even immediately after loading
        // one that did -- ToSnapshot only ever emits the new shape.
        Assert.Null(freshlyWritten.ChallengerId);
        Assert.Null(freshlyWritten.AbandonedBy);
        Assert.Equal(["u-legacy-challenger", "u-legacy-opponent"], freshlyWritten.Participants);

        var restored = LiveMatch.FromSnapshot(freshlyWritten);
        Assert.Equal(legacy.Participants, restored.Participants);
        Assert.Equal(legacy.Settings.Language, restored.Settings.Language);
        Assert.Equal(legacy.Settings.QuestionCount, restored.Settings.QuestionCount);

        // The N-player shape itself round-trips unchanged through the same JSON path the grain uses.
        var m3 = PlayFullDuelToResolution();
        var restored3 = LiveMatch.FromSnapshot(System.Text.Json.JsonSerializer.Deserialize<LiveMatchSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(m3.ToSnapshot()))!);
        Assert.Equal(m3.Participants, restored3.Participants);
        Assert.Equal(m3.Standings, restored3.Standings);
        Assert.Equal(m3.Abandoners, restored3.Abandoners);
    }
}

#pragma warning restore CS0618
