using System.Diagnostics;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Coverage for <see cref="ILiveMatchmakingGrain"/> on both of its jobs. The random queue: matching on
/// language and question count, the three non-matching cases, first-queued-sets-the-terms, expiry,
/// leaving and the two-at-once race — all on the singleton grain (key 0), each test using its own
/// (language, question count) bucket so tests never see each other's entries. Invitations: sending,
/// accepting, declining, and the two lifetime facts issue #51 changes — an invitation outlives the old
/// 45 seconds, dies with its lobby, and carries no exclusivity at all (a target can hold several at
/// once, and a challenger sending one is never blocked by another commitment).
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveMatchmakingGrainTests(LiveClusterFixture fixture)
{
    private ILiveMatchmakingGrain Queue => fixture.Cluster.GrainFactory.GetGrain<ILiveMatchmakingGrain>(0);
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

    private ILiveMatchmakingGrain Matchmaking()
        // A fresh key per test so tests never see each other's challenges or queue entries.
        => fixture.Cluster.GrainFactory.GetGrain<ILiveMatchmakingGrain>(Interlocked.Increment(ref _counter));

    private static string NewPlayerId(string tag) => $"lp-{tag}-{Guid.NewGuid():N}";

    /// <summary>Opens a fresh capacity-2 lobby owned by <paramref name="ownerId"/>, seeded with its own
    /// category so it never competes with another test's questions.</summary>
    private async Task<LiveView> NewLobbyAsync(string ownerId, string tag, Language lang = Language.En, int capacity = 2)
    {
        var cat = SeedCategory(tag, lang);
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(Guid.NewGuid().ToString("N"));
        return await grain.CreateLobbyAsync($"CODE{Interlocked.Increment(ref _n)}", ownerId, (int)lang, 10, [cat.Id], [], capacity);
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

    // --- the random queue -------------------------------------------------------------------------

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

    // --- invitations --------------------------------------------------------------------------------

    [Fact]
    public async Task A_challenge_is_sent_and_delivered_to_the_target()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("a");
        var target = NewPlayerId("b");
        var lobby = await NewLobbyAsync(challenger, "chal-send");
        var challengeId = Guid.NewGuid().ToString("N");

        var result = (LiveChallengeResult)await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);

        Assert.Equal(LiveChallengeResult.Sent, result);
        var pending = await matchmaking.PendingForAsync(target);
        Assert.Single(pending);
        Assert.Equal(challengeId, pending[0].ChallengeId);
        Assert.Equal(challenger, pending[0].ChallengerId);
        Assert.Equal(lobby.Id, pending[0].LobbyId);
        Assert.Equal(lobby.Code, pending[0].LobbyCode);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(target), e => e.Kind == "ChallengeReceived");
    }

    [Fact]
    public async Task Challenging_yourself_is_refused()
    {
        var matchmaking = Matchmaking();
        var me = NewPlayerId("solo");

        // No lobby needs to exist for this refusal: self-challenge is checked first.
        var result = (LiveChallengeResult)await matchmaking.ChallengeAsync(Guid.NewGuid().ToString("N"), me, me, "nonexistent-lobby");

        Assert.Equal(LiveChallengeResult.SelfChallenge, result);
        Assert.Empty(await matchmaking.PendingForAsync(me));
    }

    [Fact]
    public async Task Challenging_into_a_lobby_that_does_not_exist_is_refused()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("nolobby-c");
        var target = NewPlayerId("nolobby-t");

        var result = (LiveChallengeResult)await matchmaking.ChallengeAsync(Guid.NewGuid().ToString("N"), challenger, target, "nonexistent-lobby");

        Assert.Equal(LiveChallengeResult.NotFound, result);
        Assert.Empty(await matchmaking.PendingForAsync(target));
    }

    [Fact]
    public async Task Accepting_joins_the_lobby_the_challenge_points_at_and_both_are_told()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c1");
        var target = NewPlayerId("t1");
        var lobby = await NewLobbyAsync(challenger, "chal-accept");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);
        var accept = await matchmaking.AcceptAsync(challengeId, target);

        Assert.Equal((int)LiveChallengeResult.Accepted, accept.Result);
        Assert.Equal(lobby.Id, accept.MatchId);

        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(challenger), e => e.Kind == "DuelReady" && (string)e.Payload! == lobby.Id);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(target), e => e.Kind == "DuelReady" && (string)e.Payload! == lobby.Id);

        var match = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(lobby.Id);
        var view = await match.GetAsync(target);
        Assert.NotNull(view);
        Assert.Equal(challenger, view!.ChallengerId);
        Assert.Equal(target, view.OpponentId);
        Assert.NotEqual((int)LivePhase.Lobby, view.Phase); // the target already joined — a 2-seat lobby fills and starts on the spot
    }

    [Fact]
    public async Task Declining_removes_the_challenge_and_tells_the_challenger()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c2");
        var target = NewPlayerId("t2");
        var lobby = await NewLobbyAsync(challenger, "chal-decline");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);
        var result = (LiveChallengeResult)await matchmaking.DeclineAsync(challengeId, target);

        Assert.Equal(LiveChallengeResult.Declined, result);
        Assert.Empty(await matchmaking.PendingForAsync(target));
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(challenger), e => e.Kind == "ChallengeDeclined" && (string)e.Payload! == challengeId);
    }

    /// <summary>
    /// Issue #51's whole point for invitations: the old <c>ChallengeLifetime</c> was 45 seconds, right
    /// for "answer right now" and wrong for something meant to wait for someone offline. This proves it
    /// is gone — the challenge is still pending long after the old cutoff would have killed it — which
    /// is exactly what makes <c>LobbyHub.OnConnectedAsync</c>'s reconnect delivery meaningful for a
    /// friend who was not there to receive it the first time.
    /// </summary>
    [Fact]
    public async Task A_challenge_outlives_the_old_45_second_lifetime()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c3");
        var target = NewPlayerId("t3");
        var lobby = await NewLobbyAsync(challenger, "chal-outlive");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);
        Advance(TimeSpan.FromSeconds(46));

        var pending = await matchmaking.PendingForAsync(target);
        Assert.Single(pending);
        Assert.Equal(challengeId, pending[0].ChallengeId);
    }

    [Fact]
    public async Task An_untouched_challenge_expires_when_its_lobby_does_and_the_challenger_is_told()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c4");
        var target = NewPlayerId("t4");
        var lobby = await NewLobbyAsync(challenger, "chal-lobbyexpire");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);
        Assert.NotEmpty(await matchmaking.PendingForAsync(target));

        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));

        await WaitUntilAsync(async () => LiveShared.LobbyNotifier.EventsFor(challenger)
            .Any(e => e.Kind == "ChallengeExpired" && (string)e.Payload! == challengeId));

        Assert.Empty(await matchmaking.PendingForAsync(target));
    }

    [Fact]
    public async Task Accepting_an_expired_challenge_is_refused_and_joins_nobody()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c5");
        var target = NewPlayerId("t5");
        var lobby = await NewLobbyAsync(challenger, "chal-acceptexpired");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);
        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));

        // The fake clock's own timer machinery fires the grain's expiry callback the moment it is
        // advanced, ahead of anything the test calls next — so accepting now lands on whichever of
        // Expired/NotFound the expiry has already reached, not on Accepted. Either way: refused, no join.
        var accept = await matchmaking.AcceptAsync(challengeId, target);
        Assert.NotEqual((int)LiveChallengeResult.Accepted, accept.Result);
        Assert.True(accept.Result is (int)LiveChallengeResult.Expired or (int)LiveChallengeResult.NotFound);
        Assert.Null(accept.MatchId);
    }

    [Fact]
    public async Task A_challenge_still_within_its_lifetime_shows_the_time_left_measured_from_its_lobbys_own_deadline()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c6");
        var target = NewPlayerId("t6");
        var lobby = await NewLobbyAsync(challenger, "chal-timeleft");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);
        var elapsed = TimeSpan.FromMinutes(2);
        Advance(elapsed);

        var pending = await matchmaking.PendingForAsync(target);
        Assert.Single(pending);
        var remaining = pending[0].ExpiresAt - LiveShared.TimeProvider.GetUtcNow();
        var expected = LiveRules.LobbyExpires - elapsed;
        Assert.InRange(remaining.TotalSeconds, expected.TotalSeconds - 2, expected.TotalSeconds + 2);
    }

    /// <summary>
    /// The other half of issue #51's invitation change: no exclusivity at all. Being invited to
    /// several lobbies at once — or sending several invitations, or holding one while queueing for a
    /// random opponent too — is normal now, not a conflict the grain has to arbitrate.
    /// </summary>
    [Fact]
    public async Task A_target_can_hold_several_pending_invitations_at_once()
    {
        var matchmaking = Matchmaking();
        var target = NewPlayerId("multi-t");
        var challengerA = NewPlayerId("multi-ca");
        var challengerB = NewPlayerId("multi-cb");
        var lobbyA = await NewLobbyAsync(challengerA, "chal-multi-a");
        var lobbyB = await NewLobbyAsync(challengerB, "chal-multi-b");

        var resultA = (LiveChallengeResult)await matchmaking.ChallengeAsync(Guid.NewGuid().ToString("N"), challengerA, target, lobbyA.Id);
        var resultB = (LiveChallengeResult)await matchmaking.ChallengeAsync(Guid.NewGuid().ToString("N"), challengerB, target, lobbyB.Id);

        Assert.Equal(LiveChallengeResult.Sent, resultA);
        Assert.Equal(LiveChallengeResult.Sent, resultB);

        var pending = await matchmaking.PendingForAsync(target);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, c => c.LobbyId == lobbyA.Id);
        Assert.Contains(pending, c => c.LobbyId == lobbyB.Id);
    }

    [Fact]
    public async Task A_challenger_holding_a_pending_challenge_can_still_join_the_random_queue()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("free-c");
        var target = NewPlayerId("free-t");
        var lobby = await NewLobbyAsync(challenger, "chal-freequeue");

        await matchmaking.ChallengeAsync(Guid.NewGuid().ToString("N"), challenger, target, lobby.Id);

        // No commitment to be blocked by: this queues an entry rather than being refused, which is
        // exactly the difference the count proves (both a queued entry and a refusal return null).
        Assert.Null(await matchmaking.EnqueueAsync(challenger, (int)Language.En, 10, [], []));
        Assert.Equal(1, await matchmaking.WaitingCountAsync("nobody", (int)Language.En, 10));
    }

    [Fact]
    public async Task When_the_lobby_has_already_started_by_the_time_of_accept_the_challenge_is_dropped_and_both_are_told()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c10");
        var target = NewPlayerId("t10");
        var thirdParty = NewPlayerId("x10");
        var lobby = await NewLobbyAsync(challenger, "chal-full");
        var challengeId = Guid.NewGuid().ToString("N");

        await matchmaking.ChallengeAsync(challengeId, challenger, target, lobby.Id);

        // Somebody else takes the only other seat directly, filling the capacity-2 lobby before the
        // invited target ever acts on their invitation.
        var lobbyGrain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(lobby.Id);
        await lobbyGrain.JoinAsync(thirdParty);

        var accept = await matchmaking.AcceptAsync(challengeId, target);

        Assert.Equal((int)LiveChallengeResult.DuelFailed, accept.Result);
        Assert.Null(accept.MatchId);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(challenger), e => e.Kind == "ChallengeFailed" && (string)e.Payload! == challengeId);
        Assert.Contains(LiveShared.LobbyNotifier.EventsFor(target), e => e.Kind == "ChallengeFailed" && (string)e.Payload! == challengeId);
    }

    [Fact]
    public async Task Challenging_into_a_lobby_that_has_already_started_is_refused()
    {
        var matchmaking = Matchmaking();
        var challenger = NewPlayerId("c11");
        var target = NewPlayerId("t11");
        var thirdParty = NewPlayerId("x11");
        var lobby = await NewLobbyAsync(challenger, "chal-started");

        var lobbyGrain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(lobby.Id);
        await lobbyGrain.JoinAsync(thirdParty); // fills the capacity-2 lobby and starts it

        var result = (LiveChallengeResult)await matchmaking.ChallengeAsync(Guid.NewGuid().ToString("N"), challenger, target, lobby.Id);

        Assert.Equal(LiveChallengeResult.NotFound, result);
        Assert.Empty(await matchmaking.PendingForAsync(target));
    }
}
