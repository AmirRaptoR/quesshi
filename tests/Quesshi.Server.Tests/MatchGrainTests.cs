using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public class MatchGrainTests(ClusterFixture fixture)
{
    private const string Amir = "p-amir";
    private const string Sara = "p-sara";

    private IMatchGrain NewMatch(out string id, out List<string> questionIds)
    {
        id = Guid.NewGuid().ToString("N");
        questionIds = SeedQuestions(id);
        return fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
    }

    /// <summary>Six questions where the correct answer is always index 0, so tests can pick deliberately.</summary>
    private static List<string> SeedQuestions(string prefix)
    {
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

        await PlayAsync(grain, Amir, correctCount: 5);
        await PlayAsync(grain, Sara, correctCount: 2);

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.Resolved, view!.State);
        Assert.Equal(Amir, view.WinnerId);

        var archived = Shared.Archive.Items.Single(m => m.Id == id);
        Assert.Equal(MatchState.Resolved, archived.State);
        Assert.True(archived.ChallengerScore > archived.OpponentScore);
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
        Assert.Equal(Sara, rehydrated!.OpponentId);
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

        await PlayAsync(grain, Amir, correctCount: 6);
        await PlayAsync(grain, guest.Id, correctCount: 3);

        var view = await grain.GetAsync(guest.Id);
        Assert.True(view!.Runs.Single(r => r.PlayerId == guest.Id).Score > 0);

        Assert.True(Shared.Leaderboard.Scores.ContainsKey(Amir));
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(guest.Id));
    }
}
