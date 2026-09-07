using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Coverage for <see cref="ILiveLobbyGrain"/>: matching on language and question count, the three
/// non-matching cases, first-queued-sets-the-terms, expiry, leaving and the two-at-once race. One
/// singleton grain (key 0) is shared across every test in this class — each test uses its own
/// (language, question count) bucket so tests never see each other's queue entries.
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
}
