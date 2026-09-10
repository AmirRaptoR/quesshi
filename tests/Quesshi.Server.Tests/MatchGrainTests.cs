using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public class MatchGrainTests(ClusterFixture fixture)
{
    private const string Amir = "p-amir";
    private const string Sara = "p-sara";
    private const string Vahid = "p-vahid";
    private const string Stranger = "p-stranger2";

    private IMatchGrain NewMatch(out string id, out List<string> questionIds)
    {
        id = Guid.NewGuid().ToString("N");
        questionIds = SeedQuestions(id);
        return fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
    }

    /// <summary>A capacity-aware lobby, created through <see cref="IMatchGrain.CreateLobbyAsync"/> with
    /// no question set drawn yet — seeds a fresh bank first, so a later auto-start or
    /// <c>StartAsync</c> always has enough questions to draw regardless of what other tests already
    /// pulled from the shared bank.</summary>
    private async Task<(IMatchGrain Grain, string Id)> NewLobbyAsync(string code, string owner, int capacity)
    {
        var id = Guid.NewGuid().ToString("N");
        SeedQuestions(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
        await grain.CreateLobbyAsync(code, owner, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], capacity);
        return (grain, id);
    }

    /// <summary>Six questions where the correct answer is always index 0, so tests can pick deliberately.</summary>
    private static List<string> SeedQuestions(string prefix)
    {
        if (Shared.Categories.Items.All(c => c.Id != "geography"))
            Shared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            Shared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, Shared.Clock.Now,
                status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        return ids;
    }

    private static async Task PlayAsync(IMatchGrain grain, string player, int correctCount)
    {
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var served = await grain.ServeNextAsync(player);
            Assert.NotNull(served);
            await grain.AnswerAsync(player, served!.Slot, slot < correctCount ? 0 : 1);
        }
    }

    [Fact]
    public async Task A_full_duel_resolves_and_lands_in_the_archive()
    {
        var grain = NewMatch(out var id, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE01");
        Assert.True(await grain.JoinAsync(Sara));
        Assert.True(await grain.StartAsync(Amir));

        await PlayAsync(grain, Amir, correctCount: 5);
        await PlayAsync(grain, Sara, correctCount: 2);

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.Resolved, view!.State);
        Assert.Equal(Amir, view.WinnerId);

        var archived = Shared.Archive.Items.Single(m => m.Id == id);
        Assert.Equal(MatchState.Resolved, archived.State);
        Assert.True(archived.Results.Single(r => r.PlayerId == Amir).Score > archived.Results.Single(r => r.PlayerId == Sara).Score);
        Assert.True(Shared.Leaderboard.Scores[Amir] > 0);
    }

    /// <summary>
    /// The acceptance criterion for issue #48's async slice, spelled out: settlement moves both
    /// <c>PlayerStats</c> and the leaderboard, not just the leaderboard the way the pre-restructuring
    /// code happened to. Uses its own ids rather than the file's shared <see cref="Amir"/>/<see cref="Sara"/>
    /// constants — most other tests in this class also drive those two through a real, distinct
    /// settlement, and <see cref="Shared.Players"/> is one process-wide store for the whole run, so an
    /// exact-count assertion (as opposed to the ">0" the other test above makes do with) needs ids no
    /// other test in the file ever settles a match for.
    /// </summary>
    [Fact]
    public async Task A_finished_async_duel_moves_player_stats_and_the_leaderboard()
    {
        const string winner = "p-settle-async-winner";
        const string loser = "p-settle-async-loser";
        await Shared.Players.UpsertAsync(Player.Register(winner, $"{winner}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(loser, $"{loser}@example.com", "Sara", Language.En, Shared.Clock.Now));

        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, winner, questionIds, "SETASY");
        await grain.JoinAsync(loser);
        await grain.StartAsync(winner);

        await PlayAsync(grain, winner, correctCount: 5);
        await PlayAsync(grain, loser, correctCount: 2);

        var winnerStats = (await Shared.Players.GetAsync(winner))!.Stats;
        var loserStats = (await Shared.Players.GetAsync(loser))!.Stats;
        Assert.Equal(1, winnerStats.Wins);
        Assert.True(winnerStats.TotalScore > 0);
        Assert.Equal(1, loserStats.Losses);

        Assert.Equal(winnerStats.TotalScore, Shared.Leaderboard.Scores[winner]);
        Assert.Equal(loserStats.TotalScore, Shared.Leaderboard.Scores[loser]);
    }

    [Fact]
    public async Task The_opponents_answers_are_not_in_the_object_you_receive_until_you_finish()
    {
        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE02");
        await grain.JoinAsync(Sara);
        await PlayAsync(grain, Amir, correctCount: 6);

        var saraSees = await grain.GetAsync(Sara);
        var amirRun = saraSees!.Runs.Single(r => r.PlayerId == Amir);
        Assert.Empty(amirRun.Choices);
        Assert.Equal(0, amirRun.Score);

        await PlayAsync(grain, Sara, correctCount: 0);

        var afterwards = await grain.GetAsync(Sara);
        var revealed = afterwards!.Runs.Single(r => r.PlayerId == Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, revealed.Choices.Count);
        Assert.True(revealed.Score > 0);
    }

    [Fact]
    public async Task Both_players_are_served_the_same_questions_in_the_same_order()
    {
        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE03");
        await grain.JoinAsync(Sara);

        var mine = new List<string>();
        var theirs = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            mine.Add((await grain.ServeNextAsync(Amir))!.QuestionId);
            await grain.AnswerAsync(Amir, slot, 0);
            theirs.Add((await grain.ServeNextAsync(Sara))!.QuestionId);
            await grain.AnswerAsync(Sara, slot, 0);
        }

        Assert.Equal(questionIds, mine);
        Assert.Equal(mine, theirs);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_the_timer_scores_nothing()
    {
        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE04");

        var served = await grain.ServeNextAsync(Amir);
        Shared.Clock.Advance(MatchRules.QuestionTime + TimeSpan.FromMinutes(1));

        var outcome = await grain.AnswerAsync(Amir, served!.Slot, 0);

        Assert.True(outcome.Correct);
        Assert.Equal(0, outcome.Score);
    }

    [Fact]
    public async Task A_stranger_cannot_play_or_join_a_taken_match()
    {
        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE05");
        await grain.JoinAsync(Sara);

        Assert.False(await grain.JoinAsync("p-stranger"));
        Assert.Null(await grain.ServeNextAsync("p-stranger"));
    }

    [Fact]
    public async Task Serving_stops_once_a_run_is_finished()
    {
        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE06");
        await PlayAsync(grain, Amir, correctCount: 6);

        Assert.Null(await grain.ServeNextAsync(Amir));
    }

    [Fact]
    public async Task A_match_in_progress_survives_the_grain_being_deactivated()
    {
        var grain = NewMatch(out var id, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "CODE07");
        await grain.JoinAsync(Sara);

        var served = await grain.ServeNextAsync(Amir);
        await grain.AnswerAsync(Amir, served!.Slot, 0);

        // Force a fresh activation: state must come back from storage, not from memory.
        await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var rehydrated = await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id).GetAsync(Amir);
        Assert.NotNull(rehydrated);
        Assert.Equal(Sara, rehydrated!.Participants[1]);
        Assert.Equal(1, rehydrated.Runs.Single(r => r.PlayerId == Amir).Answered);
    }

    [Fact]
    public async Task A_guest_keeps_their_own_result_but_stays_off_the_leaderboard()
    {
        var guest = Player.Guest("p-guest", "Sara", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(Player.Register(Amir, "amir@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(guest);

        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "GUEST1");
        await grain.JoinAsync(guest.Id);
        await grain.StartAsync(Amir);

        await PlayAsync(grain, Amir, correctCount: 6);
        await PlayAsync(grain, guest.Id, correctCount: 3);

        var view = await grain.GetAsync(guest.Id);
        Assert.True(view!.Runs.Single(r => r.PlayerId == guest.Id).Score > 0);

        Assert.True(Shared.Leaderboard.Scores.ContainsKey(Amir));
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(guest.Id));
    }

    // ---- Lobby: capacity, Start, Leave, UpdateSettings (issue #51) ----

    [Fact]
    public async Task CreateLobbyAsync_opens_a_lobby_with_no_questions_drawn_and_only_the_owner_seated()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY1", Amir, capacity: 3);

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, view!.State);
        Assert.Empty(view.QuestionIds); // nothing drawn yet
    }

    [Fact]
    public async Task JoinAsync_seats_up_to_capacity_and_refuses_a_latecomer()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY2", Amir, capacity: 3);

        Assert.True(await grain.JoinAsync(Sara));
        Assert.True(await grain.JoinAsync(Vahid)); // fills capacity, still waiting for Start
        Assert.False(await grain.JoinAsync(Stranger));
        Assert.True(await grain.JoinAsync(Sara)); // idempotent for someone already seated
    }

    /// <summary>
    /// The async twin of <c>LiveMatchGrainTests.Two_concurrent_joins_for_the_last_seat_leave_exactly_one_seated</c>:
    /// Orleans serialises every call into a single grain activation's turn queue, so this proves the
    /// capacity guard added to <c>Match.Join</c> decides the race deterministically.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_joins_for_the_last_seat_leave_exactly_one_seated()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY12", Amir, capacity: 2);

        var results = await Task.WhenAll(grain.JoinAsync(Sara), grain.JoinAsync(Vahid));

        Assert.Single(results, true);
        Assert.Single(results, false);

        var view = await grain.GetAsync(Amir);
        Assert.Equal(2, view!.Participants.Count);
    }

    /// <summary>The async twin of <c>LiveMatchGrainTests</c>'s own capacity-shrink-racing-a-join test:
    /// either order the single grain activation's turn queue serves the two calls in, the result must
    /// leave <c>Participants.Count</c> no higher than whatever <c>Capacity</c> ends up being.</summary>
    [Fact]
    public async Task A_capacity_shrink_racing_a_join_never_leaves_more_participants_than_the_final_capacity()
    {
        var (grain, id) = await NewLobbyAsync("MLOBBY17", Amir, capacity: 3);
        SeedQuestions(id + "-extra");
        await grain.JoinAsync(Sara); // 2 seated; capacity 3 has exactly one open seat

        var join = grain.JoinAsync(Vahid);
        var shrink = grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], 2);
        await Task.WhenAll(join, shrink);

        var view = await grain.GetAsync(Amir);
        Assert.True(view!.Participants.Count <= view.Capacity,
            $"Participants.Count={view.Participants.Count} exceeded Capacity={view.Capacity}");
    }

    [Fact]
    public async Task Reaching_capacity_no_longer_auto_starts_but_Start_draws_a_real_playable_question_set()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY3", Amir, capacity: 3);

        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // the third seat fills capacity

        var afterFill = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, afterFill!.State); // full, but still waiting for Start
        Assert.Empty(afterFill.QuestionIds); // nothing drawn until Start

        Assert.True(await grain.StartAsync(Amir));
        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(MatchRules.QuestionsPerMatch, view.QuestionIds.Count); // Start drew the question set

        var served = await grain.ServeNextAsync(Vahid); // the third seat is a real, playable participant
        Assert.NotNull(served);
    }

    /// <summary>
    /// The core of issue #104: a two-seat lobby used to start the instant its second seat filled,
    /// indistinguishable from a bigger lobby's owner never getting to press Start. It no longer does.
    /// </summary>
    [Fact]
    public async Task A_capacity_two_match_stays_open_once_full_until_the_owner_presses_Start()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY4", Amir, capacity: 2);

        Assert.True(await grain.JoinAsync(Sara));

        var afterJoin = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, afterJoin!.State);
        Assert.Empty(afterJoin.QuestionIds);

        Assert.True(await grain.StartAsync(Amir));
        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(MatchRules.QuestionsPerMatch, view.QuestionIds.Count);

        await PlayAsync(grain, Amir, correctCount: 6);
        await PlayAsync(grain, Sara, correctCount: 2);

        var resolved = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.Resolved, resolved!.State);
        Assert.Equal(Amir, resolved.WinnerId);
    }

    [Fact]
    public async Task StartAsync_is_owner_only_and_requires_two_seated()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY5", Amir, capacity: 3);

        Assert.False(await grain.StartAsync(Amir)); // only the owner is seated so far

        await grain.JoinAsync(Sara);
        Assert.False(await grain.StartAsync(Sara)); // seated, but not the owner

        Assert.True(await grain.StartAsync(Amir));
        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(MatchRules.QuestionsPerMatch, view.QuestionIds.Count); // Start drew the question set
    }

    [Fact]
    public async Task StartAsync_is_refused_once_the_lobby_has_already_started()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY6", Amir, capacity: 2);
        await grain.JoinAsync(Sara);
        Assert.True(await grain.StartAsync(Amir));

        Assert.False(await grain.StartAsync(Amir)); // already started
    }

    [Fact]
    public async Task LeaveAsync_frees_a_seat_for_someone_else_to_take()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY7", Amir, capacity: 3);
        await grain.JoinAsync(Sara); // one seat still open

        Assert.True(await grain.LeaveAsync(Sara));
        var afterLeave = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, afterLeave!.State);

        // The freed seat is really free: someone new can take it.
        Assert.True(await grain.JoinAsync(Vahid));
    }

    [Fact]
    public async Task LeaveAsync_by_the_owner_ends_the_lobby_as_no_contest_with_no_ownership_transfer()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY8", Amir, capacity: 3);
        await grain.JoinAsync(Sara); // one seat still open

        Assert.True(await grain.LeaveAsync(Amir));

        var view = await grain.GetAsync(Sara); // Sara is still a participant even though the duel is over
        Assert.Equal((int)MatchState.NoContest, view!.State);
    }

    /// <summary>
    /// Before Cancel existed, MatchState.NoContest was reachable on Match only in the abstract — step 1
    /// left it "supported but produced by nothing". A lone owner can play through the pre-lobby
    /// compatibility overload (questions already drawn, exactly like The_challenger_can_play_before_
    /// anyone_joins) while still AwaitingOpponent, so cancelling after playing some is a real sequence,
    /// not a hypothetical: MatchGrain.SettleAsync must not score that as a Loss just because
    /// WinnerId/IsDraw both read "nobody in first place" for a cancelled lobby.
    /// </summary>
    [Fact]
    public async Task LeaveAsync_by_the_owner_after_playing_some_of_a_pre_drawn_lobby_settles_nothing()
    {
        // Ids of its own, never used by any other test in this file: Shared.Players/Shared.Leaderboard
        // are one process-wide store for the whole run (see A_finished_async_duel_moves_player_stats_
        // and_the_leaderboard's own remarks), and this test's whole point is that settlement never
        // touches either of them.
        var grain = NewMatch(out _, out var questionIds);
        const string owner = "p-cancel-owner";
        await grain.CreateAsync((int)Language.En, owner, questionIds, "MLOBBY8B");

        var served = await grain.ServeNextAsync(owner);
        await grain.AnswerAsync(owner, served!.Slot, 0);

        Assert.True(await grain.LeaveAsync(owner));

        var view = await grain.GetAsync(owner);
        Assert.Equal((int)MatchState.NoContest, view!.State);

        // Nobody is credited or penalised for a cancelled lobby.
        Assert.Null(await Shared.Players.GetAsync(owner));
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(owner));
    }

    [Fact]
    public async Task LeaveAsync_is_refused_for_a_stranger_and_once_the_match_has_started()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY9", Amir, capacity: 2);
        Assert.False(await grain.LeaveAsync(Stranger)); // never seated

        await grain.JoinAsync(Sara);
        Assert.True(await grain.StartAsync(Amir));
        Assert.False(await grain.LeaveAsync(Sara)); // no longer in the lobby
    }

    [Fact]
    public async Task UpdateSettingsAsync_owner_only_and_refused_once_questions_are_drawn()
    {
        var (grain, id) = await NewLobbyAsync("MLOBBY10", Amir, capacity: 3);
        SeedQuestions(id + "-extra"); // 20 total distinct En/geography questions, regardless of test order

        Assert.False(await grain.UpdateSettingsAsync(Sara, (int)Language.En, 20, [], [], null)); // not the owner
        Assert.True(await grain.UpdateSettingsAsync(Amir, (int)Language.En, 20, [], [], null));

        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // fills capacity, but no longer draws on its own (issue #104)
        Assert.True(await grain.StartAsync(Amir)); // Start draws 20 questions, per the updated settings

        var view = await grain.GetAsync(Amir);
        Assert.Equal(20, view!.QuestionIds.Count);

        // Questions are drawn now, so settings can no longer change.
        Assert.False(await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], null));
    }

    /// <summary>
    /// The async twin of <c>LiveMatchGrainTests</c>'s own equal-settings no-op test: a lobby whose
    /// question set is already drawn while still <c>AwaitingOpponent</c> — exactly the shape
    /// <c>CreateAsync</c> builds — can still have its capacity widened, because the settings half is
    /// attempted only when the requested settings actually differ from the lobby's own.
    /// </summary>
    [Fact]
    public async Task UpdateSettingsAsync_widens_capacity_alongside_the_lobbys_own_unchanged_settings_once_drawn()
    {
        var grain = NewMatch(out _, out var questionIds);
        await grain.CreateAsync((int)Language.En, Amir, questionIds, "MLOBBY13");

        var ok = await grain.UpdateSettingsAsync(Amir, (int)Language.En, questionIds.Count, [], [], 4);
        Assert.True(ok);

        var view = await grain.GetAsync(Amir);
        Assert.Equal(4, view!.Capacity);
        Assert.Equal((int)MatchState.AwaitingOpponent, view.State); // untouched otherwise
    }

    /// <summary>Atomic apply-both-or-neither (issue #104): a capacity half that fails (below the seated
    /// count) must leave the settings half unapplied too, even though the settings half alone would
    /// have succeeded.</summary>
    [Fact]
    public async Task UpdateSettingsAsync_refuses_both_halves_when_the_capacity_half_alone_would_fail()
    {
        var (grain, id) = await NewLobbyAsync("MLOBBY14", Amir, capacity: 3);
        SeedQuestions(id + "-extra");
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // 3 seated

        var ok = await grain.UpdateSettingsAsync(Amir, (int)Language.En, 20, [], [], 2); // 2 < 3 seated
        Assert.False(ok);

        var view = await grain.GetAsync(Amir);
        Assert.Equal(3, view!.Capacity); // unchanged

        Assert.True(await grain.StartAsync(Amir));
        var afterStart = await grain.GetAsync(Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, afterStart!.QuestionIds.Count); // settings half never applied either
    }

    [Fact]
    public async Task UpdateSettingsAsync_omitting_capacity_leaves_it_unchanged()
    {
        var (grain, _) = await NewLobbyAsync("MLOBBY15", Amir, capacity: 3);

        Assert.True(await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], null));

        var view = await grain.GetAsync(Amir);
        Assert.Equal(3, view!.Capacity);
    }

    [Fact]
    public async Task A_capacity_change_survives_deactivation_and_reactivation()
    {
        var (grain, id) = await NewLobbyAsync("MLOBBY16", Amir, capacity: 2);
        Assert.True(await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], 5));

        await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var view = await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id).GetAsync(Amir);
        Assert.Equal(5, view!.Capacity);
    }

    [Fact]
    public async Task An_unjoined_lobby_forfeits_after_48_hours_via_the_reminder()
    {
        var (grain, id) = await NewLobbyAsync("MLOBBY11", Amir, capacity: 2);

        Shared.Clock.Advance(MatchRules.ForfeitAfter + TimeSpan.FromMinutes(1));

        // Drives the same reminder ReceiveReminder is registered under in CreateAsync/CreateLobbyAsync,
        // without waiting on Orleans's own real-time reminder schedule for a 48-hour due time.
        await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id)
            .AsReference<Orleans.IRemindable>().ReceiveReminder("forfeit", default);

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.Forfeited, view!.State);
    }
}
