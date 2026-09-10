using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Storage;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="LiveMatchSettlementTests"/> drives <c>LiveMatchSettlement</c> directly against a
/// hand-built <see cref="LiveMatch"/> and never touches a grain. These tests are the wiring itself:
/// <c>LiveMatchGrain.SettleAsync</c>'s one call site into it, the durable checkpoint that survives a
/// storage failure mid-settlement without anyone reopening the duel, and the tri-state legacy-record
/// guard that keeps a pre-upgrade duel from ever being settled a second time.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveMatchSettlementWiringTests(LiveClusterFixture fixture)
{
    private ILiveMatchGrain NewGrain(out string id, out List<string> questionIds)
    {
        id = Guid.NewGuid().ToString("N");
        questionIds = SeedQuestions(id);
        return fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
    }

    /// <summary>Ten questions where the correct answer is always index 0, the same convention every
    /// other live grain test file uses.</summary>
    private static List<string> SeedQuestions(string prefix)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != "geography"))
            LiveShared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                explanation: "because", status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        return ids;
    }

    private static void Advance(TimeSpan by) => LiveShared.TimeProvider.Advance(by);

    private static async Task<LiveView> WaitForAsync(ILiveMatchGrain grain, string asPlayer, Func<LiveView, bool> ready, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        LiveView? last = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            last = await grain.GetAsync(asPlayer);
            if (last is not null && ready(last)) return last;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not reached within {timeoutMs}ms. Last phase={last?.Phase}, state={last?.State}, rounds={last?.Rounds.Count}.");
    }

    private static async Task RegisterAsync(string id, string displayName)
        => await LiveShared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", displayName, Language.En, LiveShared.TimeProvider.GetUtcNow()));

    /// <summary>
    /// Plays a fresh duel all the way through the last round's second answer — the challenger always
    /// correct, the opponent correct only for <paramref name="opponentCorrectCount"/> of them, so both
    /// sides bank a distinct, non-zero score and a settlement effect landing is never mistaken for a
    /// player who simply never scored. Deliberately stops with the duel sitting in <see
    /// cref="LivePhase.Reveal"/> for the last round rather than <see cref="MatchState.Resolved"/>:
    /// <see cref="LiveMatch.Answer"/> is the only thing that can end a duel from inside a call the test
    /// awaits directly, and <c>LiveMatch.OpenNextRoundOrFinish</c> — the one call that actually flips
    /// the last round's Reveal into Resolved — only ever runs from <c>Advance</c>, which every caller in
    /// <see cref="LiveMatchGrain"/> reaches only through a background timer tick except <c>Answer</c>
    /// itself, which runs it first before doing anything else. So the caller finishes the transition
    /// with one more direct call, exactly the way a genuinely late answer would.
    /// </summary>
    private async Task<string> PlayToRevealOfLastRoundAsync(
        string challenger, string opponent, int opponentCorrectCount)
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync(id.ToUpperInvariant(), (int)Language.En, challenger, questionIds);
        await grain.JoinAsync(opponent);
        await grain.StartAsync(challenger);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, challenger, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            await grain.AnswerAsync(challenger, slot, 0);
            await grain.AnswerAsync(opponent, slot, slot < opponentCorrectCount ? 0 : 1);
            await WaitForAsync(grain, challenger, v => v.Phase == (int)LivePhase.Reveal);
            if (slot < MatchRules.QuestionsPerMatch - 1)
            {
                Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));
                await WaitForAsync(grain, challenger, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == slot + 1);
            }
        }

        return id;
    }

    /// <summary>
    /// Crosses the last round's Reveal into Resolved by forcing a deactivation and reactivation rather
    /// than just advancing the clock and calling back in: with the grain still alive, advancing the
    /// clock only makes its own background timer eligible to fire the same transition, and racing an
    /// explicit call against that timer is exactly as unpredictable as it sounds — whichever gets there
    /// first, a lost race means the timer already made the transition (and, on the injected failures
    /// these tests use, already ate the exception) before the test's own call ever ran. Deactivating
    /// first removes the timer from the race entirely: reactivating is <see cref="LiveMatchGrain.OnActivateAsync"/>
    /// calling <c>LiveMatch.Advance</c> once, synchronously, as part of the very call this method awaits
    /// — the same "reactivation loads a match that just became over" shape a real process restart mid-
    /// settlement would produce, and a settlement failure surfaces as an exception on that call.
    /// </summary>
    private async Task CrossIntoResolvedByReactivationAndExpectAsync(string id, string anyParticipant)
    {
        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300); // long enough for the deactivation to actually complete, as elsewhere in this suite

        Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(anyParticipant));
    }

    // ---- The thing whose absence went unnoticed: settlement never ran at all ----

    [Fact]
    public async Task A_duel_that_ends_in_abandonment_moves_stats_the_leaderboard_and_the_penalty_through_the_grain()
    {
        var amir = $"lpw-abandon-amir-{Guid.NewGuid():N}";
        var sara = $"lpw-abandon-sara-{Guid.NewGuid():N}";
        await RegisterAsync(amir, "Amir");
        await RegisterAsync(sara, "Sara");

        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync(id.ToUpperInvariant(), (int)Language.En, amir, questionIds);
        await grain.JoinAsync(sara);
        await grain.StartAsync(amir);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        // Amir keeps answering correctly, resetting his own miss streak every round; Sara never
        // answers at all, until her miss streak alone is enough to abandon her (LiveMatch.CloseRound:
        // one silent side is Abandoned, both would be NoContest).
        LiveView view;
        for (var round = 0; ; round++)
        {
            await grain.AnswerAsync(amir, round, 0);
            Advance(MatchRules.QuestionTime + MatchRules.NetworkGrace + TimeSpan.FromSeconds(1)); // closes the round
            view = await WaitForAsync(grain, amir, v => v.State == (int)MatchState.Abandoned || (v.Phase == (int)LivePhase.Question && v.RoundIndex == round + 1));
            if (view.State == (int)MatchState.Abandoned) break;
            Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50)); // opens the next round
        }

        Assert.Equal([sara], view.AbandonedBy);
        Assert.Equal(amir, view.WinnerId);

        var amirStats = (await LiveShared.Players.GetAsync(amir))!.Stats;
        var saraStats = (await LiveShared.Players.GetAsync(sara))!.Stats;

        Assert.Equal(1, amirStats.Wins); // PlayerStats moved
        Assert.True(amirStats.TotalScore > 0);
        Assert.Equal(1, saraStats.Losses);
        Assert.Single((await LiveShared.Players.GetAsync(sara))!.Abandonments); // the penalty ran, not just a loss

        Assert.Equal(amirStats.TotalScore, LiveShared.Leaderboard.Scores[amir]); // the leaderboard moved
        // The quitter still projects — she is a registered player, not a guest — just at the zero the
        // forfeit and the floored penalty leave her with.
        Assert.Equal(0, LiveShared.Leaderboard.Scores[sara]);
    }

    // ---- Settlement has to survive a crash halfway through ----

    [Fact]
    public async Task Failing_storage_between_two_participants_effects_still_settles_both_exactly_once_on_recovery_without_anyone_reopening_the_match()
    {
        var challenger = $"lpw-between-c-{Guid.NewGuid():N}";
        var opponent = $"lpw-between-o-{Guid.NewGuid():N}";
        await RegisterAsync(challenger, "Amir");
        await RegisterAsync(opponent, "Sara");

        var id = await PlayToRevealOfLastRoundAsync(challenger, opponent, opponentCorrectCount: 1);

        // The opponent is the second participant LiveMatchSettlement walks (Participants yields
        // ChallengerId then OpponentId), so this fails the write for the participant settled *after*
        // the challenger — a storage failure landing between two participants' effects.
        LiveShared.Players.FailNextUpsertFor.Add(opponent);

        await CrossIntoResolvedByReactivationAndExpectAsync(id, challenger);

        var challengerStats = (await LiveShared.Players.GetAsync(challenger))!.Stats;
        Assert.Equal(1, challengerStats.Wins); // the first participant's effect committed
        Assert.Equal(challengerStats.TotalScore, LiveShared.Leaderboard.Scores[challenger]);
        Assert.Equal(0, (await LiveShared.Players.GetAsync(opponent))!.Stats.TotalScore); // the second never landed
        Assert.False(LiveShared.Leaderboard.Scores.ContainsKey(opponent));

        // Nobody calls back into the duel. The safety-net reminder — kept armed because settlement, not
        // IsOver, decides when it can go — is what LiveMatchGrain.ReceiveReminder uses to retry.
        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.IRemindable>().ReceiveReminder("live-safety-net", default);

        var challengerAfter = (await LiveShared.Players.GetAsync(challenger))!.Stats;
        var opponentAfter = (await LiveShared.Players.GetAsync(opponent))!.Stats;
        Assert.Equal(1, challengerAfter.Wins); // not re-applied a second time by the retry
        Assert.Equal(challengerStats.TotalScore, challengerAfter.TotalScore);
        Assert.Equal(1, opponentAfter.Losses); // settled for the first time on the retry
        Assert.True(opponentAfter.TotalScore > 0);
        Assert.Equal(challengerAfter.TotalScore, LiveShared.Leaderboard.Scores[challenger]);
        Assert.Equal(opponentAfter.TotalScore, LiveShared.Leaderboard.Scores[opponent]);
    }

    [Fact]
    public async Task Failing_storage_between_an_effect_and_the_checkpoint_save_still_settles_everyone_exactly_once_on_recovery()
    {
        var challenger = $"lpw-ckpt-c-{Guid.NewGuid():N}";
        var opponent = $"lpw-ckpt-o-{Guid.NewGuid():N}";
        await RegisterAsync(challenger, "Amir");
        await RegisterAsync(opponent, "Sara");

        var id = await PlayToRevealOfLastRoundAsync(challenger, opponent, opponentCorrectCount: 1);

        // The challenger is the *first* participant LiveMatchSettlement walks. Its stat mutation lands
        // in Mongo (TryRecordSettledMatch's own UpsertAsync, inside PlayerGrain.SettleMatchAsync) before
        // the leaderboard write that follows it throws — so the effect committed, but the exception
        // reaches LiveMatchGrain before it can record the challenger in SettledPlayers or move on to the
        // opponent at all. This is the failure the grain checkpoint alone (with no per-player dedup
        // underneath) could not survive; PlayerGrain's own settled-match marker is what makes it safe.
        LiveShared.Leaderboard.FailNextSetFor.Add(challenger);

        await CrossIntoResolvedByReactivationAndExpectAsync(id, opponent);

        var challengerScoreAfterFirstAttempt = (await LiveShared.Players.GetAsync(challenger))!.Stats.TotalScore;
        Assert.True(challengerScoreAfterFirstAttempt > 0); // the stat mutation itself landed
        Assert.False(LiveShared.Leaderboard.Scores.ContainsKey(challenger)); // ...but the leaderboard write never did
        Assert.Equal(0, (await LiveShared.Players.GetAsync(opponent))!.Stats.TotalScore); // never even attempted

        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.IRemindable>().ReceiveReminder("live-safety-net", default);

        var challengerStats = (await LiveShared.Players.GetAsync(challenger))!.Stats;
        var opponentStats = (await LiveShared.Players.GetAsync(opponent))!.Stats;
        Assert.Equal(1, challengerStats.Wins); // the dedup guard, not the grain's own checkpoint, is what kept this a no-op
        Assert.Equal(challengerScoreAfterFirstAttempt, challengerStats.TotalScore);
        Assert.Equal(challengerStats.TotalScore, LiveShared.Leaderboard.Scores[challenger]); // now repaired
        Assert.Equal(1, opponentStats.Losses); // settled for the first time on the retry
        Assert.Equal(opponentStats.TotalScore, LiveShared.Leaderboard.Scores[opponent]);
    }

    // ---- Historical matches must not be settled a second time ----

    [Fact]
    public async Task Activating_a_pre_upgrade_finished_record_with_no_settlement_block_applies_nothing()
    {
        var challenger = $"lpw-legacy-c-{Guid.NewGuid():N}";
        var opponent = $"lpw-legacy-o-{Guid.NewGuid():N}";
        await RegisterAsync(challenger, "Amir");
        await RegisterAsync(opponent, "Sara");

        // A finished duel built purely as a domain object — never through the grain — so its snapshot
        // is exactly what old code (before SettledPlayers/SettlementComplete existed) would have saved:
        // a LiveMatchStateRecord with only Json populated, SettlementComplete absent from the record
        // entirely rather than merely false.
        var start = LiveShared.TimeProvider.GetUtcNow();
        var ids = SeedQuestions("legacy-" + Guid.NewGuid().ToString("N"));
        var settings = DuelSettings.Create(Language.En, ids.Count, [], []);
        var m = LiveMatch.Create(Guid.NewGuid().ToString("N"), "LEGACY1", challenger, settings, capacity: 2, start);
        m.DrawQuestions(ids);
        m.Join(opponent, start);
        m.Start(challenger, start);
        var now = start + LiveRules.StartCountdown;
        m.Advance(now); // opens round 0
        for (var slot = 0; slot < ids.Count; slot++)
        {
            m.Answer(challenger, slot, 0, true, now, MatchRules.LevelForSlot(slot));
            m.Answer(opponent, slot, 1, false, now, MatchRules.LevelForSlot(slot));
            now += LiveRules.RevealTime;
            m.Advance(now);
        }
        Assert.Equal(MatchState.Resolved, m.State);
        var originalScore = m.Score(challenger);

        var legacyRecord = new LiveMatchStateRecord { Json = JsonSerializer.Serialize(m.ToSnapshot()) };
        Assert.Null(legacyRecord.SettlementComplete); // the pre-upgrade shape this whole test exists to prove is safe

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(m.Id);
        var storage = ((Orleans.TestingHost.InProcessSiloHandle)fixture.Cluster.Primary).ServiceProvider
            .GetRequiredKeyedService<IGrainStorage>("hot");
        await storage.WriteStateAsync("live", grain.GetGrainId(), new GrainState<LiveMatchStateRecord>(legacyRecord));

        // Activating the grain is the only thing this test does to it — no player reopens anything.
        var view = await grain.GetAsync(challenger);

        Assert.NotNull(view);
        Assert.Equal((int)MatchState.Resolved, view!.State);
        Assert.Equal(originalScore, view.Players.Single(p => p.PlayerId == challenger).Score); // untouched

        Assert.Equal(0, (await LiveShared.Players.GetAsync(challenger))!.Stats.TotalScore); // nothing applied
        Assert.Equal(0, (await LiveShared.Players.GetAsync(opponent))!.Stats.TotalScore);
        Assert.False(LiveShared.Leaderboard.Scores.ContainsKey(challenger));
        Assert.False(LiveShared.Leaderboard.Scores.ContainsKey(opponent));
    }
}
