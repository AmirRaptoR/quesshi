using Quesshi.Domain;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// The live equivalent of <c>MatchGrain.SettleAsync</c>: everything that happens once, when a live
/// duel ends. <see cref="LiveMatchSettlement"/> is a plain class, not a grain, so these tests drive a
/// <see cref="LiveMatch"/> by hand and settle it directly — exactly the shape the future
/// <c>ILiveMatchGrain</c> (issue #10) will call into at its one marked settlement call site.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class LiveMatchSettlementTests(ClusterFixture fixture)
{
    private LiveMatchSettlement Sut => new(fixture.Cluster.GrainFactory, Shared.Questions, Shared.Archive);

    /// <summary>Ten questions where the correct answer is always index 0, matching MatchGrainTests' own bank.</summary>
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

    /// <summary>A capacity-aware lobby with its set already drawn — what the deleted pre-lobby
    /// <c>LiveMatch.Create</c> overload used to build directly for a two-player duel, now the
    /// settings-aware constructor plus <see cref="LiveMatch.DrawQuestions"/>, for any capacity.</summary>
    private static LiveMatch NewLobby(string id, string ownerId, IReadOnlyList<string> questionIds, int capacity, DateTimeOffset start)
    {
        var settings = DuelSettings.Create(Language.En, questionIds.Count, [], []);
        var m = LiveMatch.Create(id, id.ToUpperInvariant(), ownerId, settings, capacity, start);
        m.DrawQuestions(questionIds);
        return m;
    }

    /// <summary>A duel to settle. Settlement never reads the share code or the language off the duel —
    /// <see cref="LiveMatchSettlement.IndexAsync"/> is handed the language separately — so both are
    /// fixed here rather than restated at every call site; the code is derived from the id so two
    /// duels in one test are still distinct.</summary>
    private static LiveMatch NewDuel(string id, string challengerId, IReadOnlyList<string> questionIds, DateTimeOffset start)
        => NewLobby(id, challengerId, questionIds, capacity: 2, start);

    /// <summary>Plays a capacity-3 duel to its natural end the same way <see cref="PlayToResolved"/>
    /// does for two — every round answered by all three, instantly.</summary>
    private static LiveMatch PlayThreePlayerDuelToResolved(string id, List<string> questionIds,
        string a, string b, string c, int aCorrect, int bCorrect, int cCorrect, DateTimeOffset start)
    {
        var m = NewLobby(id, a, questionIds, capacity: 3, start);
        m.Join(b, start);
        m.Join(c, start); // fills capacity -- auto-starts, exactly as a 1v1's second join does
        var now = start + LiveRules.StartCountdown;
        m.Advance(now); // opens round 0

        for (var slot = 0; slot < questionIds.Count; slot++)
        {
            m.Answer(a, slot, slot < aCorrect ? 0 : 1, slot < aCorrect, now, MatchRules.LevelForSlot(slot));
            m.Answer(b, slot, slot < bCorrect ? 0 : 1, slot < bCorrect, now, MatchRules.LevelForSlot(slot));
            m.Answer(c, slot, slot < cCorrect ? 0 : 1, slot < cCorrect, now, MatchRules.LevelForSlot(slot));
            now += LiveRules.RevealTime;
            m.Advance(now); // opens the next round, or resolves on the last one
        }

        return m;
    }

    /// <summary>Plays a live duel to its natural end: every round answered by both sides, instantly, so
    /// every correct answer scores the same maximum the async path would for an equally instant answer.</summary>
    private static LiveMatch PlayToResolved(string id, List<string> questionIds, string a, string b, int aCorrect, int bCorrect, DateTimeOffset start)
    {
        var m = NewDuel(id, a, questionIds, start);
        m.Join(b, start);
        var now = start + LiveRules.StartCountdown;
        m.Advance(now); // opens round 0

        for (var slot = 0; slot < questionIds.Count; slot++)
        {
            m.Answer(a, slot, slot < aCorrect ? 0 : 1, slot < aCorrect, now, MatchRules.LevelForSlot(slot));
            m.Answer(b, slot, slot < bCorrect ? 0 : 1, slot < bCorrect, now, MatchRules.LevelForSlot(slot));
            now += LiveRules.RevealTime;
            m.Advance(now); // opens the next round, or resolves on the last one
        }

        return m;
    }

    /// <summary>Answers every round for <paramref name="answerer"/> only, until the silent side racks up
    /// enough consecutive misses to be abandoned.</summary>
    private static void AbandonBySilence(LiveMatch m, string answerer, ref DateTimeOffset now)
    {
        for (var round = 0; round < MatchRules.QuestionsPerMatch && m.State != MatchState.Abandoned; round++)
        {
            var slot = m.CurrentRound!.Slot;
            m.Answer(answerer, slot, 0, true, now, MatchRules.LevelForSlot(slot));
            now += MatchRules.QuestionTime + MatchRules.NetworkGrace + TimeSpan.FromSeconds(1);
            m.Advance(now); // closes the round; abandons once the silent side's miss streak is enough
            if (m.State == MatchState.Abandoned) return;

            now += LiveRules.RevealTime;
            m.Advance(now); // opens the next round
        }
    }

    private static async Task PlayAsyncDuelAsync(IMatchGrain grain, string player, int correctCount)
    {
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var served = await grain.ServeNextAsync(player);
            await grain.AnswerAsync(player, served!.Slot, slot < correctCount ? 0 : 1);
        }
    }

    [Fact]
    public async Task A_resolved_live_duel_has_the_same_effect_as_an_equivalent_async_one()
    {
        const string asyncA = "p-parity-async-a";
        const string asyncB = "p-parity-async-b";
        const string liveA = "p-parity-live-a";
        const string liveB = "p-parity-live-b";
        foreach (var id in new[] { asyncA, asyncB, liveA, liveB })
            await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", id, Language.En, Shared.Clock.Now));

        var asyncGrain = fixture.Cluster.GrainFactory.GetGrain<IMatchGrain>("parity-async");
        await asyncGrain.CreateAsync((int)Language.En, asyncA, SeedQuestions("parity-async"), "PARASY");
        await asyncGrain.JoinAsync(asyncB);
        await PlayAsyncDuelAsync(asyncGrain, asyncA, correctCount: 6);
        await PlayAsyncDuelAsync(asyncGrain, asyncB, correctCount: 3);

        var live = PlayToResolved("parity-live", SeedQuestions("parity-live"), liveA, liveB, aCorrect: 6, bCorrect: 3, Shared.Clock.Now);
        await Sut.SettleAsync(live, Language.En);

        var (asyncWinner, asyncLoser) = ((await Shared.Players.GetAsync(asyncA))!.Stats, (await Shared.Players.GetAsync(asyncB))!.Stats);
        var (liveWinner, liveLoser) = ((await Shared.Players.GetAsync(liveA))!.Stats, (await Shared.Players.GetAsync(liveB))!.Stats);

        Assert.Equal(asyncWinner.Wins, liveWinner.Wins);
        Assert.Equal(asyncWinner.TotalScore, liveWinner.TotalScore);
        Assert.Equal(asyncLoser.Losses, liveLoser.Losses);
        Assert.Equal(asyncLoser.TotalScore, liveLoser.TotalScore);

        Assert.Equal(Shared.Leaderboard.Scores[asyncA], Shared.Leaderboard.Scores[liveA]);
        Assert.Equal(Shared.Leaderboard.Scores[asyncB], Shared.Leaderboard.Scores[liveB]);
        Assert.True(Shared.Leaderboard.Scores[liveA] > 0);
    }

    [Fact]
    public async Task A_guest_keeps_their_own_result_but_stays_off_the_leaderboard()
    {
        const string host = "p-liveguest-host";
        var guest = Player.Guest("p-liveguest-guest", "Sara", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(Player.Register(host, $"{host}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(guest);

        var live = PlayToResolved("liveguest", SeedQuestions("liveguest"), host, guest.Id, aCorrect: 6, bCorrect: 2, Shared.Clock.Now);
        await Sut.SettleAsync(live, Language.En);

        Assert.True((await Shared.Players.GetAsync(guest.Id))!.Stats.TotalScore > 0);
        Assert.True(Shared.Leaderboard.Scores.ContainsKey(host));
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(guest.Id));
    }

    [Fact]
    public async Task A_no_contest_leaves_stats_and_the_leaderboard_byte_identical()
    {
        const string a = "p-nocontest-a";
        const string b = "p-nocontest-b";
        await Shared.Players.UpsertAsync(Player.Register(a, $"{a}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(b, $"{b}@example.com", "Sara", Language.En, Shared.Clock.Now));

        var start = Shared.Clock.Now;
        var m = NewDuel("nocontest-1", a, SeedQuestions("nocontest"), start);
        m.Join(b, start);
        // Nobody ever answers; the lobby/round clock runs out and the duel becomes a no-contest.
        m.Advance(start + LiveRules.StartCountdown + MatchRules.QuestionTime + LiveRules.StaleAfter + LiveRules.StaleAfter);
        Assert.Equal(MatchState.NoContest, m.State);

        var beforeA = (await Shared.Players.GetAsync(a))!.Stats;
        var beforeB = (await Shared.Players.GetAsync(b))!.Stats;

        await Sut.SettleAsync(m, Language.En);

        Assert.Equal(beforeA, (await Shared.Players.GetAsync(a))!.Stats);
        Assert.Equal(beforeB, (await Shared.Players.GetAsync(b))!.Stats);
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(a));
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(b));

        // Still findable: a no-contest is mirrored into the archive exactly as a resolved duel is.
        var archived = Shared.Archive.Items.Single(x => x.Id == "nocontest-1");
        Assert.True(archived.IsLive);
        Assert.Equal(MatchState.NoContest, archived.State);
    }

    [Fact]
    public async Task Everybody_walking_out_still_pays_the_abandonment_penalty_even_though_nobody_is_credited()
    {
        const string a = "p-allabandon-a";
        const string b = "p-allabandon-b";
        const string c = "p-allabandon-c";
        foreach (var id in new[] { a, b, c })
            await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", id, Language.En, Shared.Clock.Now));

        var start = Shared.Clock.Now;
        var m = NewLobby("allabandon-1", a, SeedQuestions("allabandon"), capacity: 3, start);
        m.Join(b, start);
        m.Join(c, start);

        // Nobody answers, but the clock is walked forward in steps far shorter than StaleAfter, so
        // each round closes on its own schedule and every player earns three real misses. A single
        // long jump would instead read as a process that was away and settle as Stale — a different
        // reason, deliberately carrying no penalty, which is exactly the distinction under test.
        var now = start;
        while (!m.IsOver && now - start < TimeSpan.FromMinutes(5))
        {
            now += TimeSpan.FromSeconds(10);
            m.Advance(now);
        }

        Assert.Equal(MatchState.NoContest, m.State);
        Assert.Equal(NoContestReason.AllAbandoned, m.Reason);

        await Sut.SettleAsync(m, Language.En);

        foreach (var id in new[] { a, b, c })
        {
            var player = (await Shared.Players.GetAsync(id))!;

            // The penalty landed: nobody gets to dodge it by agreeing to quit together, which is the
            // whole reason this case is settled at all.
            Assert.Single(player.Abandonments);

            // But nothing was credited. A no-contest ranks nobody, so there is no win, loss or draw
            // to record and no score to bank -- only the penalty crosses.
            Assert.Equal(0, player.Stats.Wins);
            Assert.Equal(0, player.Stats.Losses);
            Assert.Equal(0, player.Stats.Draws);
        }
    }

    [Fact]
    public async Task An_abandoned_duel_charges_the_quitter_a_loss_of_zero_and_gives_the_winner_their_real_score_and_no_bonus()
    {
        const string quitter = "p-abandon-quitter";
        const string winner = "p-abandon-winner";
        await Shared.Players.UpsertAsync(Player.Register(quitter, $"{quitter}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(winner, $"{winner}@example.com", "Sara", Language.En, Shared.Clock.Now));

        var start = Shared.Clock.Now;
        var ids = SeedQuestions("abandon");
        var m = NewDuel("abandon-1", quitter, ids, start);
        m.Join(winner, start);
        var now = start + LiveRules.StartCountdown;
        m.Advance(now); // opens round 0

        AbandonBySilence(m, winner, ref now);

        Assert.Equal(MatchState.Abandoned, m.State);
        Assert.Equal(quitter, m.Abandoners.Single().PlayerId);
        Assert.Equal(winner, m.WinnerId);
        var winnerEarned = m.Score(winner);
        Assert.True(winnerEarned > 0);

        await Sut.SettleAsync(m, Language.En);

        var quitterStats = (await Shared.Players.GetAsync(quitter))!.Stats;
        var winnerStats = (await Shared.Players.GetAsync(winner))!.Stats;

        Assert.Equal(1, quitterStats.Losses);
        Assert.Equal(0, quitterStats.TotalScore);
        Assert.False(Shared.Leaderboard.Scores.TryGetValue(quitter, out var quitterScore) && quitterScore != 0);

        Assert.Equal(1, winnerStats.Wins);
        Assert.Equal(winnerEarned, winnerStats.TotalScore);
        Assert.Equal(winnerEarned, Shared.Leaderboard.Scores[winner]); // the win and nothing more: no bonus
    }

    [Fact]
    public async Task A_repeat_quitter_pays_the_escalating_penalty_on_top_of_the_forfeit()
    {
        const string quitter = "p-repeat-quitter";
        const string winner = "p-repeat-winner";
        var player = Player.Register(quitter, $"{quitter}@example.com", "Amir", Language.En, Shared.Clock.Now);
        player.RecordResult(MatchOutcome.Win, 1000); // banked from an earlier duel
        await Shared.Players.UpsertAsync(player);
        await Shared.Players.UpsertAsync(Player.Register(winner, $"{winner}@example.com", "Sara", Language.En, Shared.Clock.Now));
        await Shared.Leaderboard.SetAsync(quitter, 1000); // the same banked score, mirrored onto the board

        // A first abandonment on record already, so this settlement is the costly second one.
        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(quitter).SettleMatchAsync("repeat-0", null, 0, [], [], Shared.Clock.Now);
        var beforeSecond = (await Shared.Players.GetAsync(quitter))!.Stats.TotalScore;

        var start = Shared.Clock.Now.AddHours(1);
        var ids = SeedQuestions("repeat");
        var m = NewDuel("repeat-1", quitter, ids, start);
        m.Join(winner, start);
        var now = start + LiveRules.StartCountdown;
        m.Advance(now);
        AbandonBySilence(m, winner, ref now);
        Assert.Equal(MatchState.Abandoned, m.State);

        await Sut.SettleAsync(m, Language.En);

        var after = (await Shared.Players.GetAsync(quitter))!.Stats.TotalScore;
        Assert.Equal(Math.Max(0, beforeSecond - 200), after); // the second abandonment costs 200
        Assert.Equal(after, Shared.Leaderboard.Scores[quitter]);
    }

    /// <summary>
    /// A guest still pays for walking away — the stats penalty is charged to their own record exactly
    /// as a signed-in quitter's is — but a guest was never on the leaderboard to begin with, and
    /// penalising must not be the way one gets on it.
    /// </summary>
    [Fact]
    public async Task A_guest_who_abandons_pays_the_stats_penalty_but_still_never_reaches_the_leaderboard()
    {
        var quitter = Player.Guest("p-guestabandon-quitter", "Amir", Language.En, Shared.Clock.Now);
        quitter.RecordResult(MatchOutcome.Win, 1000); // banked from an earlier duel
        await Shared.Players.UpsertAsync(quitter);
        const string winner = "p-guestabandon-winner";
        await Shared.Players.UpsertAsync(Player.Register(winner, $"{winner}@example.com", "Sara", Language.En, Shared.Clock.Now));

        // A first abandonment on record already, so this settlement is the costly second one.
        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(quitter.Id).SettleMatchAsync("guestabandon-0", null, 0, [], [], Shared.Clock.Now);
        var beforeSecond = (await Shared.Players.GetAsync(quitter.Id))!.Stats.TotalScore;

        var start = Shared.Clock.Now.AddHours(1);
        var ids = SeedQuestions("guestabandon");
        var m = NewDuel("guestabandon-1", quitter.Id, ids, start);
        m.Join(winner, start);
        var now = start + LiveRules.StartCountdown;
        m.Advance(now);
        AbandonBySilence(m, winner, ref now);
        Assert.Equal(MatchState.Abandoned, m.State);

        await Sut.SettleAsync(m, Language.En);

        var after = (await Shared.Players.GetAsync(quitter.Id))!.Stats.TotalScore;
        Assert.Equal(Math.Max(0, beforeSecond - 200), after); // the stats penalty still applies
        Assert.False(Shared.Leaderboard.Scores.ContainsKey(quitter.Id)); // but no entry was ever created
    }

    [Fact]
    public async Task A_live_duel_is_mirrored_into_the_archive_on_start_and_again_on_end()
    {
        var start = Shared.Clock.Now;
        var ids = SeedQuestions("mirror");
        var m = NewDuel("mirror-1", "p-mirror-a", ids, start);

        await Sut.IndexAsync(m, Language.En);
        var onStart = Shared.Archive.Items.Single(x => x.Id == "mirror-1");
        Assert.True(onStart.IsLive);
        Assert.Equal(MatchState.AwaitingOpponent, onStart.State);

        m.Join("p-mirror-b", start);
        var live = PlayToResolved("mirror-1", ids, "p-mirror-a", "p-mirror-b", aCorrect: 4, bCorrect: 1, start);
        await Sut.SettleAsync(live, Language.En);

        var onEnd = Shared.Archive.Items.Single(x => x.Id == "mirror-1");
        Assert.Equal(MatchState.Resolved, onEnd.State);
        Assert.True(onEnd.Results.Single(r => r.PlayerId == "p-mirror-a").Score > 0);
    }

    /// <summary>
    /// The regression this issue exists for: <see cref="LiveMatchSettlement"/> used to walk a private
    /// two-entry <c>Participants(LiveMatch)</c> iterator that yielded only <c>ChallengerId</c> and
    /// <c>OpponentId</c>, so a capacity&gt;2 duel's third-and-later seats were silently skipped by
    /// settlement outright — no stats, no leaderboard entry, no archived result, ever, for anyone past
    /// the second seat, with nothing failing loudly because the obsolete accessors it read from still
    /// compile. This drives a real capacity-3 duel to <see cref="MatchState.Resolved"/> and checks
    /// every one of the three actually got settled, not just the first two.
    /// </summary>
    [Fact]
    public async Task A_resolved_capacity_three_duel_settles_every_participant_not_just_the_first_two()
    {
        const string first = "p-cap3-first";
        const string second = "p-cap3-second";
        const string third = "p-cap3-third";
        foreach (var id in new[] { first, second, third })
            await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", id, Language.En, Shared.Clock.Now));

        var m = PlayThreePlayerDuelToResolved("cap3-settle", SeedQuestions("cap3-settle"),
            first, second, third, aCorrect: 8, bCorrect: 4, cCorrect: 1, Shared.Clock.Now);
        Assert.Equal(MatchState.Resolved, m.State);

        await Sut.SettleAsync(m, Language.En);

        // Stats: all three, not just the first two -- the third seat is the one the bug dropped.
        var firstStats = (await Shared.Players.GetAsync(first))!.Stats;
        var secondStats = (await Shared.Players.GetAsync(second))!.Stats;
        var thirdStats = (await Shared.Players.GetAsync(third))!.Stats;
        Assert.Equal(1, firstStats.Wins);
        Assert.Equal(1, secondStats.Losses);
        Assert.Equal(1, thirdStats.Losses); // was 0/0/untouched before this fix
        Assert.True(thirdStats.TotalScore > 0);

        // Leaderboard: all three get a real entry, ranked by their own actual score.
        Assert.True(Shared.Leaderboard.Scores.ContainsKey(third));
        Assert.True(Shared.Leaderboard.Scores[first] > Shared.Leaderboard.Scores[second]);
        Assert.True(Shared.Leaderboard.Scores[second] > Shared.Leaderboard.Scores[third]);

        // Archived result: a real, correctly-ranked ParticipantResult for every one of the three.
        var archived = Shared.Archive.Items.Single(x => x.Id == "cap3-settle");
        Assert.Equal(3, archived.Results.Count);
        var byId = archived.Results.ToDictionary(r => r.PlayerId);
        Assert.Equal(1, byId[first].Place);
        Assert.Equal(MatchOutcome.Win, byId[first].Outcome);
        Assert.Equal(3, byId[third].Place);
        Assert.Equal(MatchOutcome.Loss, byId[third].Outcome);
        Assert.True(byId[third].Score > 0);
    }
}
