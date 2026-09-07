using System.Diagnostics;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Coverage for <see cref="ILiveLobbyGrain"/> on both of its jobs. The random queue: matching on
/// language and question count, the three non-matching cases, first-queued-sets-the-terms, expiry,
/// leaving and the two-at-once race — all on the singleton grain (key 0), each test using its own
/// (language, question count) bucket so tests never see each other's entries. Friend challenges:
/// sending, accepting, declining, expiry and the mutual exclusion between holding a challenge and
/// waiting in the queue — each on a grain key of its own, because a challenge is not partitioned by
/// bucket the way a queue entry is and two tests sharing a grain would see each other's.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveLobbyGrainTests(LiveClusterFixture fixture)
{
    private ILiveLobbyGrain Queue => fixture.Cluster.GrainFactory.GetGrain<ILiveLobbyGrain>(0);
    private static int _n;

    private static void Advance(TimeSpan by) => LiveShared.TimeProvider.Advance(by);

    private static async Task WaitForAsync(Func<Task<bool>> ready, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await ready()) return;
            await Task.Delay(25);
        }
        Assert.Fail("Timed out waiting for condition.");
    }

    /// <summary>A category of its own per test, seeded with enough approved English questions to fill
    /// any of MatchRules' valid lengths, so different tests' duels never draw on each other's pool.</summary>
    private static Category SeedCategory(string suffix, Language lang = Language.En, int count = 100)
    {
        var id = $"llg-cat-{suffix}-{Interlocked.Increment(ref _n)}";
        var category = new Category(id, $"دسته {suffix}", $"Category {suffix}", "globe", "#336699");
        LiveShared.Categories.Items.Add(category);

        for (var i = 0; i < count; i++)
        {
            var qid = $"{id}-q{i}";
            LiveShared.Questions.Items.Add(Question.Create(qid, lang, id, MatchRules.LevelForSlot(i, count),
                $"q {i}", ["right", "w1", "w2", "w3"], 0, LiveShared.TimeProvider.GetUtcNow(), status: QuestionStatus.Approved));
        }

        return category;
    }

    private static int _counter;

    private ILiveLobbyGrain Lobby()
        // A fresh key per test so tests never see each other's challenges or queue entries.
        => fixture.Cluster.GrainFactory.GetGrain<ILiveLobbyGrain>(Interlocked.Increment(ref _counter));

    private static string NewPlayerId(string tag) => $"lp-{tag}-{Guid.NewGuid():N}";

    /// <summary>Ten geography questions in the given language, enough for one duel.</summary>
    private static void SeedQuestions(string prefix, Language lang = Language.En)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != "geography"))
            LiveShared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            LiveShared.Questions.Items.Add(Question.Create(qid, lang, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                explanation: "because", status: QuestionStatus.Approved));
        }
    }

    /// <summary>Polls real wall-clock time until a condition holds — a grain timer fires off the fake
    /// clock asynchronously relative to the calling thread, the same way LiveMatchGrainTests waits.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> ready, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await ready()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not reached within {timeoutMs}ms.");
    }

    [Fact]
    public async Task Two_players_in_the_same_language_and_count_are_matched_into_one_duel_out_of_lobby()
    {
        var cat = SeedCategory("match");
        var p1 = "llg-p1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-p2-" + Interlocked.Increment(ref _n);

        var first = await Queue.EnqueueAsync(p1, (int)Language.En, 10, [cat.Id], []);
        Assert.Null(first);

        var second = await Queue.EnqueueAsync(p2, (int)Language.En, 10, [cat.Id], []);
        Assert.NotNull(second);

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(second);
        var view1 = await grain.GetAsync(p1);
        var view2 = await grain.GetAsync(p2);

        Assert.NotNull(view1);
        Assert.NotNull(view2);
        Assert.Equal(p1, view1!.ChallengerId);
        Assert.Equal(p2, view1.OpponentId);
        Assert.NotEqual((int)LivePhase.Lobby, view1.Phase);
        Assert.Equal((int)MatchState.InProgress, view1.State);

        Assert.Equal("Matched", LiveShared.LobbyNotifier.EventsFor(p1).Last().Kind);
        Assert.Equal(second, LiveShared.LobbyNotifier.EventsFor(p1).Last().Payload);
        Assert.Equal("Matched", LiveShared.LobbyNotifier.EventsFor(p2).Last().Kind);
        Assert.Equal(second, LiveShared.LobbyNotifier.EventsFor(p2).Last().Payload);
    }

    [Fact]
    public async Task Different_languages_are_not_matched_and_both_stay_queued()
    {
        var cat = SeedCategory("lang");
        var pEn = "llg-en-" + Interlocked.Increment(ref _n);
        var pFa = "llg-fa-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(pEn, (int)Language.En, 20, [cat.Id], []));
        Assert.Null(await Queue.EnqueueAsync(pFa, (int)Language.Fa, 20, [cat.Id], []));

        Assert.Equal(0, await Queue.WaitingCountAsync(pEn, (int)Language.En, 20));
        Assert.Equal(0, await Queue.WaitingCountAsync(pFa, (int)Language.Fa, 20));

        var observer = "llg-obs-" + Interlocked.Increment(ref _n);
        Assert.Equal(1, await Queue.WaitingCountAsync(observer, (int)Language.En, 20));
        Assert.Equal(1, await Queue.WaitingCountAsync(observer, (int)Language.Fa, 20));
    }

    [Fact]
    public async Task Different_question_counts_are_not_matched_and_both_stay_queued()
    {
        var cat = SeedCategory("count");
        var p30 = "llg-c30-" + Interlocked.Increment(ref _n);
        var p40 = "llg-c40-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p30, (int)Language.En, 30, [cat.Id], []));
        Assert.Null(await Queue.EnqueueAsync(p40, (int)Language.En, 40, [cat.Id], []));

        var observer = "llg-obs2-" + Interlocked.Increment(ref _n);
        Assert.Equal(1, await Queue.WaitingCountAsync(observer, (int)Language.En, 30));
        Assert.Equal(1, await Queue.WaitingCountAsync(observer, (int)Language.En, 40));
    }

    [Fact]
    public async Task Disjoint_categories_still_match_and_the_duel_uses_the_first_queued_players_categories()
    {
        var firstCat = SeedCategory("first");
        var secondCat = SeedCategory("second");
        var p1 = "llg-df1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-df2-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.En, 50, [firstCat.Id], []));
        var matchId = await Queue.EnqueueAsync(p2, (int)Language.En, 50, [secondCat.Id], []);
        Assert.NotNull(matchId);

        var view = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId).GetAsync(p1);
        Assert.NotNull(view);
        foreach (var round in view!.Rounds)
        {
            var question = LiveShared.Questions.Items.First(q => q.Id == round.QuestionId);
            Assert.Equal(firstCat.Id, question.CategoryId);
        }
    }

    [Fact]
    public async Task Queueing_twice_with_the_same_player_id_leaves_exactly_one_entry()
    {
        var cat = SeedCategory("dup");
        var p1 = "llg-dup-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Nl, 10, [cat.Id], []));
        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Nl, 10, [cat.Id], []));

        var observer = "llg-obs3-" + Interlocked.Increment(ref _n);
        Assert.Equal(1, await Queue.WaitingCountAsync(observer, (int)Language.Nl, 10));
    }

    [Fact]
    public async Task A_player_is_never_matched_with_themselves()
    {
        var cat = SeedCategory("self");
        var p1 = "llg-self-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Nl, 20, [cat.Id], []));
        // Same player calling Enqueue again in the same bucket must never be handed its own entry.
        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Nl, 20, [cat.Id], []));
    }

    [Fact]
    public async Task Two_players_enqueueing_at_the_same_instant_produce_exactly_one_duel_between_them()
    {
        var cat = SeedCategory("race", Language.Nl);
        var p1 = "llg-race1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-race2-" + Interlocked.Increment(ref _n);

        var t1 = Queue.EnqueueAsync(p1, (int)Language.Nl, 30, [cat.Id], []);
        var t2 = Queue.EnqueueAsync(p2, (int)Language.Nl, 30, [cat.Id], []);
        var results = await Task.WhenAll(t1, t2);

        var matchIds = results.Where(r => r is not null).ToList();
        Assert.Single(matchIds);

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchIds[0]!);
        var view1 = await grain.GetAsync(p1);
        var view2 = await grain.GetAsync(p2);
        Assert.NotNull(view1);
        Assert.NotNull(view2);
        Assert.Equal(view1!.Id, view2!.Id);
    }

    [Fact]
    public async Task When_the_question_set_cannot_be_built_neither_player_remains_queued_and_both_are_told()
    {
        var scarce = new Category($"llg-scarce-{Interlocked.Increment(ref _n)}", "کمیاب", "Scarce", "globe", "#336699");
        LiveShared.Categories.Items.Add(scarce);
        // Only 3 questions in a category asked to fill a 40-question duel with no fallback category
        // named, so QuestionSetBuilder.BuildAsync must throw NotEnoughQuestionsException.
        for (var i = 0; i < 3; i++)
        {
            var qid = $"{scarce.Id}-q{i}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.Nl, scarce.Id, MatchRules.LevelForSlot(i, 3),
                $"scarce {i}", ["right", "w1", "w2", "w3"], 0, LiveShared.TimeProvider.GetUtcNow(), status: QuestionStatus.Approved));
        }

        var p1 = "llg-scarce1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-scarce2-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Nl, 40, [scarce.Id], []));
        var result = await Queue.EnqueueAsync(p2, (int)Language.Nl, 40, [scarce.Id], []);

        Assert.Null(result);
        Assert.Equal("QueueFailed", LiveShared.LobbyNotifier.EventsFor(p1).Last().Kind);
        Assert.Equal("QueueFailed", LiveShared.LobbyNotifier.EventsFor(p2).Last().Kind);

        var observer = "llg-obs4-" + Interlocked.Increment(ref _n);
        Assert.Equal(0, await Queue.WaitingCountAsync(observer, (int)Language.Nl, 40));
    }

    [Fact]
    public async Task An_entry_not_refreshed_for_sixty_seconds_is_gone_and_never_handed_to_an_arriving_player()
    {
        var cat = SeedCategory("ttl");
        var p1 = "llg-ttl1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-ttl2-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.En, 100, [cat.Id], []));
        Advance(TimeSpan.FromSeconds(61));

        var observer = "llg-obs5-" + Interlocked.Increment(ref _n);
        Assert.Equal(0, await Queue.WaitingCountAsync(observer, (int)Language.En, 100));

        // p2 arrives after the entry has gone stale: nobody is waiting to be handed to them.
        var result = await Queue.EnqueueAsync(p2, (int)Language.En, 100, [cat.Id], []);
        Assert.Null(result);
    }

    [Fact]
    public async Task A_heartbeat_inside_the_window_keeps_the_entry_and_an_arriving_player_still_matches_it()
    {
        var cat = SeedCategory("hb", Language.Fa);
        var p1 = "llg-hb1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-hb2-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Fa, 10, [cat.Id], []));
        Advance(TimeSpan.FromSeconds(45));
        await Queue.HeartbeatAsync(p1);
        Advance(TimeSpan.FromSeconds(45)); // 90s total, but only 45s since the heartbeat

        var matchId = await Queue.EnqueueAsync(p2, (int)Language.Fa, 10, [cat.Id], []);
        Assert.NotNull(matchId);
    }

    [Fact]
    public async Task Leaving_the_queue_removes_the_entry_immediately_and_an_arriving_player_finds_nobody()
    {
        var cat = SeedCategory("leave");
        var p1 = "llg-lv1-" + Interlocked.Increment(ref _n);
        var p2 = "llg-lv2-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(p1, (int)Language.Fa, 40, [cat.Id], []));
        await Queue.LeaveAsync(p1);

        var result = await Queue.EnqueueAsync(p2, (int)Language.Fa, 40, [cat.Id], []);
        Assert.Null(result);
    }

    [Fact]
    public async Task A_stale_entry_is_pruned_by_the_grains_own_timer_with_nobody_calling_in()
    {
        var cat = SeedCategory("timer");
        var lonely = "llg-tm-lonely-" + Interlocked.Increment(ref _n);
        var stale = "llg-tm-stale-" + Interlocked.Increment(ref _n);

        Assert.Null(await Queue.EnqueueAsync(lonely, (int)Language.Fa, 30, [cat.Id], []));
        Assert.Null(await Queue.EnqueueAsync(stale, (int)Language.Fa, 30, [cat.Id], []));

        // lonely stays fresh; stale is never refreshed again and goes quiet from here on.
        Advance(TimeSpan.FromSeconds(70));
        await Queue.HeartbeatAsync(lonely);

        // Neither Enqueue nor WaitingCount is called from here on — only the grain's own timer can
        // prune stale's now-105-second-old entry and tell lonely their waiting count dropped to zero.
        Advance(TimeSpan.FromSeconds(60));

        await WaitForAsync(async () => LiveShared.LobbyNotifier.EventsFor(lonely).Any(e => e.Kind == "QueueCount" && Equals(e.Payload, 0)));
    }


    [Fact]
    public async Task A_challenge_is_sent_and_delivered_to_the_target()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("a");
        var target = NewPlayerId("b");
        var challengeId = Guid.NewGuid().ToString("N");

        var result = (LiveChallengeResult)await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.En, 10, ["geography"], []);

        Assert.Equal(LiveChallengeResult.Sent, result);
        var pending = await lobby.PendingForAsync(target);
        Assert.NotNull(pending);
        Assert.Equal(challengeId, pending!.ChallengeId);
        Assert.Equal(challenger, pending.ChallengerId);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(target), e => e.Kind == "ChallengeReceived");
    }

    [Fact]
    public async Task Challenging_yourself_is_refused()
    {
        var lobby = Lobby();
        var me = NewPlayerId("solo");

        var result = (LiveChallengeResult)await lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), me, me, (int)Language.En, 10, [], []);

        Assert.Equal(LiveChallengeResult.SelfChallenge, result);
        Assert.Null(await lobby.PendingForAsync(me));
    }

    [Fact]
    public async Task Accepting_creates_a_duel_out_of_lobby_using_the_challengers_settings_and_both_are_told()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c1");
        var target = NewPlayerId("t1");
        var challengeId = Guid.NewGuid().ToString("N");
        SeedQuestions(challengeId);

        await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.En, 10, ["geography"], []);
        var accept = await lobby.AcceptAsync(challengeId, target);

        Assert.Equal((int)LiveChallengeResult.Accepted, accept.Result);
        Assert.NotNull(accept.MatchId);

        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(challenger), e => e.Kind == "DuelReady" && (string)e.Payload! == accept.MatchId);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(target), e => e.Kind == "DuelReady" && (string)e.Payload! == accept.MatchId);

        var match = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(accept.MatchId!);
        var view = await match.GetAsync(target);
        Assert.NotNull(view);
        Assert.Equal(challenger, view!.ChallengerId);
        Assert.Equal(target, view.OpponentId);
        Assert.NotEqual((int)LivePhase.Lobby, view.Phase); // the target already joined — counting down, not waiting
    }

    [Fact]
    public async Task Declining_removes_the_challenge_and_tells_the_challenger()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c2");
        var target = NewPlayerId("t2");
        var challengeId = Guid.NewGuid().ToString("N");

        await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.En, 10, [], []);
        var result = (LiveChallengeResult)await lobby.DeclineAsync(challengeId, target);

        Assert.Equal(LiveChallengeResult.Declined, result);
        Assert.Null(await lobby.PendingForAsync(target));
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(challenger), e => e.Kind == "ChallengeDeclined" && (string)e.Payload! == challengeId);
    }

    [Fact]
    public async Task An_untouched_challenge_expires_45_seconds_after_it_was_sent_and_the_challenger_is_told()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c3");
        var target = NewPlayerId("t3");
        var challengeId = Guid.NewGuid().ToString("N");

        await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.En, 10, [], []);
        Assert.NotNull(await lobby.PendingForAsync(target));

        Advance(TimeSpan.FromSeconds(46));

        await WaitUntilAsync(async () => LiveShared.LobbyNotifier.EventsFor(challenger)
            .Any(e => e.Kind == "ChallengeExpired" && (string)e.Payload! == challengeId));

        Assert.Null(await lobby.PendingForAsync(target));
    }

    [Fact]
    public async Task Accepting_an_expired_challenge_is_refused_and_creates_no_duel()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c4");
        var target = NewPlayerId("t4");
        var challengeId = Guid.NewGuid().ToString("N");

        await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.En, 10, [], []);
        Advance(TimeSpan.FromSeconds(46));

        // The fake clock's own timer machinery fires the grain's expiry callback the moment it is
        // advanced, ahead of anything the test calls next — so accepting now lands on whichever of
        // Expired/NotFound the expiry has already reached, not on Accepted. Either way: refused, no duel.
        var accept = await lobby.AcceptAsync(challengeId, target);
        Assert.NotEqual((int)LiveChallengeResult.Accepted, accept.Result);
        Assert.True(accept.Result is (int)LiveChallengeResult.Expired or (int)LiveChallengeResult.NotFound);
        Assert.Null(accept.MatchId);
    }

    [Fact]
    public async Task A_challenge_still_within_its_lifetime_shows_the_time_left_measured_from_when_it_was_sent()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c5");
        var target = NewPlayerId("t5");
        var challengeId = Guid.NewGuid().ToString("N");

        await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.En, 10, [], []);
        Advance(TimeSpan.FromSeconds(40));

        var pending = await lobby.PendingForAsync(target);
        Assert.NotNull(pending);
        var remaining = pending!.ExpiresAt - LiveShared.TimeProvider.GetUtcNow();
        Assert.InRange(remaining.TotalSeconds, 3, 7);
    }

    [Fact]
    public async Task A_player_already_holding_a_pending_challenge_cannot_be_put_into_a_second_one()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c6");
        var target = NewPlayerId("t6");
        var thirdParty = NewPlayerId("x6");
        var firstChallengeId = Guid.NewGuid().ToString("N");

        await lobby.ChallengeAsync(firstChallengeId, challenger, target, (int)Language.En, 10, [], []);

        // The challenger tries to send a second challenge elsewhere: refused.
        var second = (LiveChallengeResult)await lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), challenger, thirdParty, (int)Language.En, 10, [], []);
        Assert.Equal(LiveChallengeResult.CallerCommitted, second);

        // The target is challenged again by somebody else: refused too, and the first challenge is untouched.
        var third = (LiveChallengeResult)await lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), thirdParty, target, (int)Language.En, 10, [], []);
        Assert.Equal(LiveChallengeResult.TargetCommitted, third);

        var pending = await lobby.PendingForAsync(target);
        Assert.NotNull(pending);
        Assert.Equal(firstChallengeId, pending!.ChallengeId);
    }

    [Fact]
    public async Task A_player_waiting_in_the_random_queue_cannot_be_challenged()
    {
        var lobby = Lobby();
        var queued = NewPlayerId("q1");
        var challenger = NewPlayerId("c7");

        // Nobody else is waiting in this grain's bucket, so this queues rather than matching.
        Assert.Null(await lobby.EnqueueAsync(queued, (int)Language.En, 10, [], []));
        Assert.Equal(1, await lobby.WaitingCountAsync("nobody", (int)Language.En, 10));

        var result = (LiveChallengeResult)await lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), challenger, queued, (int)Language.En, 10, [], []);
        Assert.Equal(LiveChallengeResult.TargetCommitted, result);
    }

    [Fact]
    public async Task A_player_holding_a_pending_challenge_cannot_join_the_random_queue()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c8");
        var target = NewPlayerId("t8");

        await lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), challenger, target, (int)Language.En, 10, [], []);

        // EnqueueAsync reports a refusal the same way it reports "queued, nobody to match": with null.
        // What separates them is whether an entry exists afterwards, so that is what is asserted —
        // neither side of a pending challenge may leave one behind.
        Assert.Null(await lobby.EnqueueAsync(challenger, (int)Language.En, 10, [], []));
        Assert.Null(await lobby.EnqueueAsync(target, (int)Language.En, 10, [], []));
        Assert.Equal(0, await lobby.WaitingCountAsync("nobody", (int)Language.En, 10));
    }

    [Fact]
    public async Task Two_players_challenging_the_same_target_at_the_same_instant_produce_exactly_one_pending_challenge()
    {
        var lobby = Lobby();
        var target = NewPlayerId("hot");
        var challengerA = NewPlayerId("ca");
        var challengerB = NewPlayerId("cb");

        var results = await Task.WhenAll(
            lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), challengerA, target, (int)Language.En, 10, [], []),
            lobby.ChallengeAsync(Guid.NewGuid().ToString("N"), challengerB, target, (int)Language.En, 10, [], []));

        var sentCount = results.Count(r => (LiveChallengeResult)r == LiveChallengeResult.Sent);
        Assert.Equal(1, sentCount);
        Assert.Contains(results, r => (LiveChallengeResult)r == LiveChallengeResult.TargetCommitted);
    }

    [Fact]
    public async Task After_a_challenge_is_declined_both_players_are_free_again()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c9");
        var target = NewPlayerId("t9");
        var firstId = Guid.NewGuid().ToString("N");

        await lobby.ChallengeAsync(firstId, challenger, target, (int)Language.En, 10, [], []);
        await lobby.DeclineAsync(firstId, target);

        var secondId = Guid.NewGuid().ToString("N");
        var result = (LiveChallengeResult)await lobby.ChallengeAsync(secondId, challenger, target, (int)Language.En, 10, [], []);
        Assert.Equal(LiveChallengeResult.Sent, result);
    }

    [Fact]
    public async Task When_the_duel_cannot_be_built_the_challenge_is_dropped_and_both_players_are_told()
    {
        var lobby = Lobby();
        var challenger = NewPlayerId("c10");
        var target = NewPlayerId("t10");
        var challengeId = Guid.NewGuid().ToString("N");

        // Nl has no approved questions seeded anywhere in this fixture: QuestionSetBuilder cannot build a set.
        await lobby.ChallengeAsync(challengeId, challenger, target, (int)Language.Nl, 10, [], []);
        var accept = await lobby.AcceptAsync(challengeId, target);

        Assert.Equal((int)LiveChallengeResult.DuelFailed, accept.Result);
        Assert.Null(accept.MatchId);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(challenger), e => e.Kind == "ChallengeFailed" && (string)e.Payload! == challengeId);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(target), e => e.Kind == "ChallengeFailed" && (string)e.Payload! == challengeId);

        // Both are free again: the challenger's commitment is gone, so this queues an entry instead of
        // being refused — which is the difference the count proves, since either outcome returns null.
        Assert.Null(await lobby.EnqueueAsync(challenger, (int)Language.En, 10, [], []));
        Assert.Equal(1, await lobby.WaitingCountAsync("nobody", (int)Language.En, 10));
    }
}
