using System.Text.Json;
using Orleans;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Durable settlement for the async duel: issue #48's "settlement is not connected" prerequisite,
/// specifically the part that is dangerous to get wrong — recovering from a storage failure midway
/// through settling a match, and never re-settling a match that finished before this feature existed.
/// Runs on its own isolated cluster (<see cref="MatchRecoveryClusterCollection"/>) so its "hot" grain
/// storage can be a fake with a seam for failing one write and for seeding a legacy-shaped row — see
/// <see cref="FaultyMatchStorage"/> — without touching the ordinary in-memory storage every other test
/// in the assembly shares.
/// </summary>
[Collection(nameof(MatchRecoveryClusterCollection))]
public class MatchGrainRecoveryTests(MatchRecoveryClusterFixture fixture)
{
    /// <summary>Six questions where the correct answer is always index 0, matching MatchGrainTests' own bank.</summary>
    private static List<string> SeedQuestions(string prefix)
    {
        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            MatchRecoveryShared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, MatchRecoveryShared.Clock.Now,
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

    /// <summary>Deactivates the grain and waits for the deactivation to actually land, so the next
    /// grain reference forces a fresh OnActivateAsync rather than reusing the same in-memory instance —
    /// the same trick MatchGrainTests' own deactivation test uses.</summary>
    private async Task ReactivateAsync(string id)
    {
        await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);
    }

    [Fact]
    public async Task Failing_one_participants_own_effect_still_settles_both_exactly_once_on_recovery()
    {
        const string winner = "p-recover-effect-winner";
        const string loser = "p-recover-effect-loser";
        await MatchRecoveryShared.Players.UpsertAsync(Player.Register(winner, $"{winner}@example.com", "Amir", Language.En, MatchRecoveryShared.Clock.Now));
        await MatchRecoveryShared.Players.UpsertAsync(Player.Register(loser, $"{loser}@example.com", "Sara", Language.En, MatchRecoveryShared.Clock.Now));

        var id = Guid.NewGuid().ToString("N");
        var questionIds = SeedQuestions(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
        await grain.CreateAsync((int)Language.En, winner, questionIds, "RCVR01");
        await grain.JoinAsync(loser);

        // The winner finishes first: their settlement effect (inside PlayerGrain) and MatchGrain's own
        // checkpoint recording it both land for real before anything fails — the failure below sits
        // strictly between this participant's effect and the other's, not before either.
        await PlayAsync(grain, winner, correctCount: 5);

        // The loser's own settlement effect is what throws — a Mongo-level failure inside PlayerGrain,
        // nothing to do with MatchGrain's own storage — surfacing exactly when the loser's last answer
        // flips the match over and settlement runs inline.
        MatchRecoveryShared.Players.FailNextUpsertFor.Add(loser);

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch - 1; slot++)
        {
            var served = await grain.ServeNextAsync(loser);
            await grain.AnswerAsync(loser, served!.Slot, 1);
        }
        var last = await grain.ServeNextAsync(loser);
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AnswerAsync(loser, last!.Slot, 1));

        // Recovered with nobody reopening the match: a fresh activation resumes settlement on its own.
        await ReactivateAsync(id);
        await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id).GetAsync(winner);

        var winnerStats = (await MatchRecoveryShared.Players.GetAsync(winner))!.Stats;
        var loserStats = (await MatchRecoveryShared.Players.GetAsync(loser))!.Stats;
        Assert.Equal(1, winnerStats.Wins); // not 2: the winner's already-applied effect was never repeated
        Assert.Equal(1, loserStats.Losses); // not 0: the loser's failed effect was retried and landed
        Assert.Equal(winnerStats.TotalScore, MatchRecoveryShared.Leaderboard.Scores[winner]);
        Assert.Equal(loserStats.TotalScore, MatchRecoveryShared.Leaderboard.Scores[loser]);
    }

    [Fact]
    public async Task Failing_the_checkpoint_right_after_an_effect_still_settles_both_exactly_once_on_recovery()
    {
        const string winner = "p-recover-checkpoint-winner";
        const string loser = "p-recover-checkpoint-loser";
        await MatchRecoveryShared.Players.UpsertAsync(Player.Register(winner, $"{winner}@example.com", "Amir", Language.En, MatchRecoveryShared.Clock.Now));
        await MatchRecoveryShared.Players.UpsertAsync(Player.Register(loser, $"{loser}@example.com", "Sara", Language.En, MatchRecoveryShared.Clock.Now));

        var id = Guid.NewGuid().ToString("N");
        var questionIds = SeedQuestions(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
        await grain.CreateAsync((int)Language.En, winner, questionIds, "RCVR02");
        await grain.JoinAsync(loser);

        await PlayAsync(grain, winner, correctCount: 5);

        // Every slot before the last plays out normally, with storage still healthy — SaveAsync's own
        // write inside ServeNextAsync and AnswerAsync must not be the ones that fail, or this would
        // stop the match from ever reaching "over" at all rather than testing settlement recovery.
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch - 1; slot++)
        {
            var served = await grain.ServeNextAsync(loser);
            await grain.AnswerAsync(loser, served!.Slot, 1);
        }

        // MatchGrain's own storage, not PlayerGrain's, fails this time — and precisely the write right
        // after the first participant's effect, not either of the two writes before it: the last
        // ServeNextAsync's own save (1), and AnswerAsync's transition save marking the match over and
        // starting the settlement block (2). Sides() always yields the challenger (the winner here)
        // first, so failing the third write fails the checkpoint meant to record the winner — right
        // after PlayerGrain.SettleMatchAsync(winner) has already actually run and committed for real.
        var grainId = grain.GetGrainId();
        MatchRecoveryShared.Storage.FailNextWriteFor(grainId, afterSuccessfulWrites: 2);

        // Orleans wraps a storage provider's own exception in its own OrleansException rather than
        // propagating it unwrapped the way a grain-to-grain call's exception is (the other recovery
        // test above asserts InvalidOperationException directly for exactly that reason).
        var last = await grain.ServeNextAsync(loser);
        await Assert.ThrowsAsync<Orleans.Runtime.OrleansException>(() => grain.AnswerAsync(loser, last!.Slot, 1));

        // The winner's real effect committed even though MatchGrain's own checkpoint recording it did
        // not — the whole point of the per-(match, player) marker living in PlayerGrain rather than
        // only in MatchGrain's own state: this loss cannot corrupt anything, only cost the resume a
        // redundant, harmless re-attempt.
        var winnerStatsBeforeRecovery = (await MatchRecoveryShared.Players.GetAsync(winner))!.Stats;
        Assert.Equal(1, winnerStatsBeforeRecovery.Wins);

        await ReactivateAsync(id);
        await fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id).GetAsync(winner);

        var winnerStats = (await MatchRecoveryShared.Players.GetAsync(winner))!.Stats;
        var loserStats = (await MatchRecoveryShared.Players.GetAsync(loser))!.Stats;
        Assert.Equal(1, winnerStats.Wins); // re-attempted on resume, but PlayerGrain's own dedup no-ops it
        Assert.Equal(1, loserStats.Losses); // never even attempted before the crash; applied for real now
        Assert.Equal(winnerStats.TotalScore, MatchRecoveryShared.Leaderboard.Scores[winner]);
        Assert.Equal(loserStats.TotalScore, MatchRecoveryShared.Leaderboard.Scores[loser]);
    }

    /// <summary>
    /// The most dangerous case in issue #48: every match ever played before this feature shipped is
    /// still sitting in Redis with no settlement block at all, and MatchGrain is reactivated for
    /// exactly these archived rows by the async history listing. This builds a finished match directly
    /// against the domain model — never through the grain — so nothing ever calls MatchGrain.SaveAsync
    /// and <see cref="MatchStateRecord.SettlementJson"/> is left at its untouched default "", the exact
    /// shape a row written by code that predates the field is guaranteed to have, then seeds it
    /// straight into storage and activates the grain cold.
    /// </summary>
    [Fact]
    public async Task A_pre_upgrade_finished_record_with_no_settlement_block_is_never_settled()
    {
        const string playerA = "p-legacy-a";
        const string playerB = "p-legacy-b";
        await MatchRecoveryShared.Players.UpsertAsync(Player.Register(playerA, $"{playerA}@example.com", "Amir", Language.En, MatchRecoveryShared.Clock.Now));
        await MatchRecoveryShared.Players.UpsertAsync(Player.Register(playerB, $"{playerB}@example.com", "Sara", Language.En, MatchRecoveryShared.Clock.Now));

        var id = Guid.NewGuid().ToString("N");
        var questionIds = SeedQuestions(id);
        var now = MatchRecoveryShared.Clock.Now;

        var match = Match.Create(id, "LEGACY", Language.En, playerA, questionIds, now);
        match.Join(playerB, now);
        foreach (var player in new[] { playerA, playerB })
        {
            for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
            {
                var served = match.ServeNext(player, now);
                match.SubmitAnswer(player, served.Index, 0, correct: true, now);
            }
        }
        Assert.True(match.IsOver);

        // SettlementJson left at its default "" — never set — exactly what a row this old actually is.
        var legacyRecord = new MatchStateRecord { Json = JsonSerializer.Serialize(match.ToSnapshot()) };

        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>(id);
        MatchRecoveryShared.Storage.Seed("match", grain.GetGrainId(), legacyRecord);

        // Any call activates the grain fresh, running OnActivateAsync's resume-on-activation check —
        // exactly what the history listing's GetAsync does for an archived row today.
        var view = await grain.GetAsync(playerA);
        Assert.Equal((int)MatchState.Resolved, view!.State); // sanity: the seeded row is really finished

        var statsA = (await MatchRecoveryShared.Players.GetAsync(playerA))!.Stats;
        var statsB = (await MatchRecoveryShared.Players.GetAsync(playerB))!.Stats;
        Assert.Equal(PlayerStats.Empty, statsA);
        Assert.Equal(PlayerStats.Empty, statsB);
        Assert.False(MatchRecoveryShared.Leaderboard.Scores.ContainsKey(playerA));
        Assert.False(MatchRecoveryShared.Leaderboard.Scores.ContainsKey(playerB));
    }
}
