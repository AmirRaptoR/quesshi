using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Coverage for <see cref="ILiveMatchGrain.RequestRematchAsync"/>: the both-must-press handshake,
/// its idempotence, its expiry, and the two ways the second press can still fail to produce a duel
/// (the kill switch, and <c>QuestionSetBuilder</c> running dry). Guest refusal is <see cref="LiveHub"/>'s
/// own job (see the issue's technical notes) and is covered at the hub level instead.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveRematchGrainTests(LiveClusterFixture fixture)
{
    private const string Challenger = "rm-challenger";
    private const string Opponent = "rm-opponent";
    private const string Stranger = "rm-stranger";
    private static int _n;

    /// <summary>Enough approved English questions to fill any of <c>MatchRules.QuestionCountChoices</c>,
    /// so a rematch's own re-draw (default breadth, no preferred category) always has somewhere to draw from.</summary>
    private static Category SeedCategory()
    {
        var id = $"rm-cat-{Interlocked.Increment(ref _n)}";
        var category = new Category(id, "دسته", "Category", "globe", "#336699");
        LiveShared.Categories.Items.Add(category);

        for (var i = 0; i < 100; i++)
        {
            var qid = $"{id}-q{i}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, id, MatchRules.LevelForSlot(i, 100),
                $"q {i}", ["right", "w1", "w2", "w3"], 0, LiveShared.TimeProvider.GetUtcNow(), status: QuestionStatus.Approved));
        }

        return category;
    }

    private static List<string> DummyQuestionIds(string prefix, int count) => [.. Enumerable.Range(0, count).Select(i => $"{prefix}-dummy-{i}")];

    /// <summary>A finished duel between a fresh challenger/opponent pair, joined and immediately ended
    /// as a no-contest before the clock ever advances — so no round is ever opened and no question ever
    /// has to resolve.</summary>
    private async Task<(ILiveMatchGrain Grain, string ChallengerId, string OpponentId, string MatchId)> NewFinishedDuelAsync(
        Language lang = Language.En, int count = 10, bool seedQuestions = true)
    {
        var n = Interlocked.Increment(ref _n);
        var challengerId = $"{Challenger}-{n}";
        var opponentId = $"{Opponent}-{n}";
        var matchId = Guid.NewGuid().ToString("N");

        var questionIds = seedQuestions ? SeedQuestionsFor(SeedCategory().Id, count) : DummyQuestionIds(matchId, count);

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"CODE{n}", (int)lang, challengerId, questionIds);
        await grain.JoinAsync(opponentId);
        await grain.EndAsync("test");

        return (grain, challengerId, opponentId, matchId);
    }

    private static List<string> SeedQuestionsFor(string categoryId, int count)
    {
        // SeedCategory already added 100 questions under its own id; this just returns the first `count` of them.
        return [.. LiveShared.Questions.Items.Where(q => q.CategoryId == categoryId).Take(count).Select(q => q.Id)];
    }

    [Fact]
    public async Task A_non_participant_is_refused_and_no_readiness_is_recorded()
    {
        var (grain, challengerId, _, matchId) = await NewFinishedDuelAsync();

        var outcome = await grain.RequestRematchAsync(Stranger);

        Assert.Equal((int)RematchStatus.Refused, outcome.Status);
        Assert.Null(outcome.NewMatchId);

        // The stranger's press must not itself be mistaken for the challenger's own readiness.
        var second = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Waiting, second.Status);
        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchCreated");
    }

    [Fact]
    public async Task A_duel_still_in_progress_is_refused()
    {
        var n = Interlocked.Increment(ref _n);
        var challengerId = $"{Challenger}-{n}";
        var opponentId = $"{Opponent}-{n}";
        var matchId = Guid.NewGuid().ToString("N");
        var questionIds = SeedQuestionsFor(SeedCategory().Id, 10);

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"CODE{n}", (int)Language.En, challengerId, questionIds);
        await grain.JoinAsync(opponentId); // in progress, not over

        var outcome = await grain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Refused, outcome.Status);
    }

    [Fact]
    public async Task A_duel_with_no_opponent_is_refused()
    {
        var n = Interlocked.Increment(ref _n);
        var challengerId = $"{Challenger}-{n}";
        var matchId = Guid.NewGuid().ToString("N");
        var questionIds = SeedQuestionsFor(SeedCategory().Id, 10);

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"CODE{n}", (int)Language.En, challengerId, questionIds);
        await grain.CancelAsync(challengerId); // a lobby cancelled before anyone joined

        var outcome = await grain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Refused, outcome.Status);
    }

    [Fact]
    public async Task One_player_ready_starts_nothing_and_notifies_the_other_side()
    {
        var (grain, challengerId, _, matchId) = await NewFinishedDuelAsync();

        var outcome = await grain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Waiting, outcome.Status);
        Assert.Null(outcome.NewMatchId);
        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchCreated");
        Assert.Contains(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchRequested" && (string)e.Payload == challengerId);
    }

    [Fact]
    public async Task The_same_player_pressing_twice_still_only_waits()
    {
        var (grain, challengerId, _, matchId) = await NewFinishedDuelAsync();

        await grain.RequestRematchAsync(challengerId);
        var again = await grain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Waiting, again.Status);
        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchCreated");
    }

    [Fact]
    public async Task Both_players_ready_creates_exactly_one_duel_with_the_same_language_and_count_and_both_joined()
    {
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync(Language.En, 10);

        var first = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Waiting, first.Status);

        var second = await grain.RequestRematchAsync(opponentId);
        Assert.Equal((int)RematchStatus.Created, second.Status);
        Assert.NotNull(second.NewMatchId);
        Assert.NotEqual(matchId, second.NewMatchId);

        var createdEvents = LiveShared.Notifier.EventsFor(matchId).Where(e => e.Kind == "RematchCreated").ToList();
        Assert.Single(createdEvents);

        var newGrain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(second.NewMatchId!);
        var newView = await newGrain.GetAsync(challengerId);
        Assert.NotNull(newView);
        Assert.Equal(challengerId, newView!.ChallengerId);
        Assert.Equal(opponentId, newView.OpponentId);
        Assert.Equal((int)Language.En, newView.Lang);
        Assert.Equal(10, newView.TotalRounds);
        Assert.Equal((int)LivePhase.Countdown, newView.Phase);
        Assert.Equal((int)MatchState.InProgress, newView.State);
    }

    [Fact]
    public async Task Readiness_older_than_the_expiry_no_longer_completes_the_handshake()
    {
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync();

        var first = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Waiting, first.Status);

        LiveShared.TimeProvider.Advance(LiveRules.RematchExpires + TimeSpan.FromSeconds(1));

        var second = await grain.RequestRematchAsync(opponentId);

        Assert.Equal((int)RematchStatus.Waiting, second.Status);
        Assert.Null(second.NewMatchId);
        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchCreated");
    }

    [Fact]
    public async Task Kill_switch_off_creates_no_duel_clears_readiness_and_notifies_both_sides()
    {
        var settings = fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0);
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync();

        await settings.SetEnabledAsync(false);
        try
        {
            await grain.RequestRematchAsync(challengerId);
            var second = await grain.RequestRematchAsync(opponentId);

            Assert.Equal((int)RematchStatus.Failed, second.Status);
            Assert.Null(second.NewMatchId);
            Assert.Contains(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchFailed");
        }
        finally
        {
            await settings.SetEnabledAsync(true);
        }

        // Readiness was cleared by the failure: one fresh press only waits again, it does not
        // immediately complete against the stale pair.
        var afterRecovery = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Waiting, afterRecovery.Status);
    }

    [Fact]
    public async Task When_the_question_set_cannot_be_built_no_duel_is_created_and_both_sides_are_told()
    {
        // No Persian question has ever been seeded into LiveShared by any test in this assembly, and
        // a rematch's re-draw asks for the default breadth (no preferred category) — so 100 (the
        // largest valid question count) can never be filled.
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync(Language.Fa, 100, seedQuestions: false);

        await grain.RequestRematchAsync(challengerId);
        var second = await grain.RequestRematchAsync(opponentId);

        Assert.Equal((int)RematchStatus.Failed, second.Status);
        Assert.Null(second.NewMatchId);
        Assert.Contains(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchFailed");
    }
}
