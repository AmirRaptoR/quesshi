using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Coverage for <see cref="ILiveMatchGrain.RequestRematchAsync"/> under issue #51's redesign: no more
/// symmetric readiness — the first request that reaches the grain creates a lobby with this duel's
/// own settings and capacity, every later request (from anyone, however many times) lands on that
/// same lobby because its id is <em>derived</em> from the finished match's id rather than minted, and
/// every other participant is auto-invited. Guest refusal is <see cref="LiveHub"/>'s own job (see the
/// issue's technical notes) and is covered at the hub level instead.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveRematchGrainTests(LiveClusterFixture fixture)
{
    private const string Challenger = "rm-challenger";
    private const string Opponent = "rm-opponent";
    private const string Stranger = "rm-stranger";
    private static int _n;

    /// <summary>Enough approved English questions to fill any of <c>MatchRules.QuestionCountChoices</c>,
    /// so a rematch lobby's own eventual draw (default breadth, no preferred category) always has
    /// somewhere to draw from once it starts.</summary>
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

    /// <summary>A finished duel between a fresh challenger/opponent pair, joined and immediately ended
    /// as a no-contest before the clock ever advances — so no round is ever opened and no question ever
    /// has to resolve.</summary>
    private async Task<(ILiveMatchGrain Grain, string ChallengerId, string OpponentId, string MatchId)> NewFinishedDuelAsync(
        Language lang = Language.En, int count = 10)
    {
        var n = Interlocked.Increment(ref _n);
        var challengerId = $"{Challenger}-{n}";
        var opponentId = $"{Opponent}-{n}";
        var matchId = Guid.NewGuid().ToString("N");

        var questionIds = SeedQuestionsFor(SeedCategory().Id, count);

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
    public async Task A_non_participant_is_refused_and_nothing_is_created()
    {
        var (grain, _, _, matchId) = await NewFinishedDuelAsync();

        var outcome = await grain.RequestRematchAsync(Stranger);

        Assert.Equal((int)RematchStatus.Refused, outcome.Status);
        Assert.Null(outcome.NewMatchId);
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
    public async Task The_first_request_creates_a_lobby_with_the_same_language_and_count_and_invites_the_other_participant()
    {
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync(Language.En, 10);

        var outcome = await grain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Created, outcome.Status);
        Assert.NotNull(outcome.NewMatchId);
        Assert.NotEqual(matchId, outcome.NewMatchId);

        var createdEvents = LiveShared.Notifier.EventsFor(matchId).Where(e => e.Kind == "RematchCreated").ToList();
        Assert.Single(createdEvents);

        var lobbyGrain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(outcome.NewMatchId!);
        var lobbyView = await lobbyGrain.GetAsync(challengerId);
        Assert.NotNull(lobbyView);
        Assert.Equal(challengerId, lobbyView!.Participants[0]);
        Assert.Equal((int)Language.En, lobbyView.Lang);
        Assert.Equal((int)LivePhase.Lobby, lobbyView.Phase); // waiting for the invited participant, not auto-started
        Assert.Equal((int)MatchState.AwaitingOpponent, lobbyView.State);

        // The requester (now the lobby's owner) is not invited to their own lobby; the other
        // participant of the finished duel is.
        var matchmaking = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchmakingGrain>(0);
        var pending = await matchmaking.PendingForAsync(opponentId);
        Assert.Contains(pending, c => c.LobbyId == outcome.NewMatchId && c.ChallengerId == challengerId);
        Assert.Empty(await matchmaking.PendingForAsync(challengerId));
    }

    /// <summary>
    /// The acceptance-criterion test: however many participants press rematch, however many times,
    /// they all land on the one lobby the derived id names — because <c>CreateLobbyAsync</c> is
    /// idempotent and nothing is ever recorded to race over.
    /// </summary>
    [Fact]
    public async Task Repeated_rematch_requests_from_different_participants_all_land_on_one_lobby()
    {
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync();

        var first = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Created, first.Status);

        var second = await grain.RequestRematchAsync(opponentId);
        Assert.Equal((int)RematchStatus.Created, second.Status);

        var third = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Created, third.Status);

        Assert.Equal(first.NewMatchId, second.NewMatchId);
        Assert.Equal(first.NewMatchId, third.NewMatchId);
        Assert.NotEqual(matchId, first.NewMatchId);

        // Only the first call actually creates the lobby; the owner is whoever that first request
        // named — the second and third calls' own requested ownership is simply ignored.
        var lobbyGrain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(first.NewMatchId!);
        var lobbyView = await lobbyGrain.GetAsync(challengerId);
        Assert.Equal(challengerId, lobbyView!.Participants[0]);
    }

    /// <summary>Concurrent presses are exactly what the derived id (and Orleans' one-call-at-a-time
    /// guarantee per grain) has to survive without producing two lobbies.</summary>
    [Fact]
    public async Task Simultaneous_rematch_requests_still_produce_exactly_one_lobby()
    {
        var (grain, challengerId, opponentId, _) = await NewFinishedDuelAsync();

        var results = await Task.WhenAll(
            grain.RequestRematchAsync(challengerId),
            grain.RequestRematchAsync(opponentId));

        Assert.All(results, r => Assert.Equal((int)RematchStatus.Created, r.Status));
        Assert.Equal(results[0].NewMatchId, results[1].NewMatchId);
    }

    /// <summary>
    /// The chain keeps extending rather than colliding: a rematch of the rematch lobby (once *it* is
    /// over) derives a further id from its own, distinct from both the original match and its own
    /// direct rematch.
    /// </summary>
    [Fact]
    public async Task A_rematch_of_a_started_rematch_lobby_derives_a_fresh_id()
    {
        var (grain, challengerId, opponentId, matchId) = await NewFinishedDuelAsync();

        var firstRematch = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Created, firstRematch.Status);
        var lobbyId = firstRematch.NewMatchId!;

        // The invited participant joins the rematch lobby, which — at capacity 2 — starts it on the
        // spot, then it is ended so it, too, is a finished duel eligible for its own rematch.
        var lobbyGrain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(lobbyId);
        await lobbyGrain.JoinAsync(opponentId);
        await lobbyGrain.EndAsync("test");

        var secondRematch = await lobbyGrain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Created, secondRematch.Status);
        Assert.NotNull(secondRematch.NewMatchId);
        Assert.NotEqual(matchId, secondRematch.NewMatchId);
        Assert.NotEqual(lobbyId, secondRematch.NewMatchId);

        // Pressing rematch again on the *original* finished duel still lands on the first lobby,
        // proving the two chains (original->rematch1, rematch1->rematch2) never cross.
        var repeatOfFirst = await grain.RequestRematchAsync(opponentId);
        Assert.Equal(lobbyId, repeatOfFirst.NewMatchId);
    }

    [Fact]
    public async Task Kill_switch_off_creates_no_lobby_and_notifies_the_finished_duels_group()
    {
        var settings = fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0);
        var (grain, challengerId, _, matchId) = await NewFinishedDuelAsync();

        await settings.SetEnabledAsync(false);
        try
        {
            var outcome = await grain.RequestRematchAsync(challengerId);

            Assert.Equal((int)RematchStatus.Failed, outcome.Status);
            Assert.Null(outcome.NewMatchId);
            Assert.Contains(LiveShared.Notifier.EventsFor(matchId), e => e.Kind == "RematchFailed");
        }
        finally
        {
            await settings.SetEnabledAsync(true);
        }

        // Recovery: a fresh press once the switch is back on creates the lobby normally.
        var afterRecovery = await grain.RequestRematchAsync(challengerId);
        Assert.Equal((int)RematchStatus.Created, afterRecovery.Status);
        Assert.NotNull(afterRecovery.NewMatchId);
    }

    /// <summary>
    /// Issue #52's invitation-reach requirement: a guest participant of the finished duel never
    /// receives the in-app <c>ChallengeAsync</c> invitation every other participant gets — they never
    /// connect to <c>LobbyHub</c> at all, so one would sit undelivered forever — but the rematch
    /// lobby's share code still goes out to everyone, guest included, on the finished duel's own
    /// <c>RematchCreated</c> push. That code is the guest's entire invitation: the link they are
    /// reached by, exactly as the original duel was.
    /// </summary>
    [Fact]
    public async Task A_guest_participant_gets_no_in_app_invitation_but_the_rematch_code_still_reaches_everyone()
    {
        var n = Interlocked.Increment(ref _n);
        var challengerId = $"{Challenger}-{n}";
        var guestId = $"rm-guest-{n}";
        var matchId = Guid.NewGuid().ToString("N");
        var questionIds = SeedQuestionsFor(SeedCategory().Id, 10);

        LiveShared.Players.Items.Add(Player.Guest(guestId, "Guest", Language.En, LiveShared.TimeProvider.GetUtcNow()));

        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(matchId);
        await grain.CreateAsync($"CODE{n}", (int)Language.En, challengerId, questionIds);
        await grain.JoinAsync(guestId);
        await grain.EndAsync("test");

        var outcome = await grain.RequestRematchAsync(challengerId);

        Assert.Equal((int)RematchStatus.Created, outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.NewMatchCode));

        // No in-app invitation was ever minted for the guest.
        var matchmaking = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchmakingGrain>(0);
        var pending = await matchmaking.PendingForAsync(guestId);
        Assert.Empty(pending);

        // The code everyone (guest included) can join the lobby by went out on the duel's own group,
        // and it is the very code the requester's own return value carries.
        var created = LiveShared.Notifier.EventsFor(matchId).Single(e => e.Kind == "RematchCreated");
        var (newMatchId, newMatchCode) = ((string NewMatchId, string NewMatchCode))created.Payload;
        Assert.Equal(outcome.NewMatchId, newMatchId);
        Assert.Equal(outcome.NewMatchCode, newMatchCode);

        // The code genuinely resolves the rematch lobby, exactly the way any other invite link does.
        var byCode = await LiveShared.Archive.ByCodeAsync(newMatchCode);
        Assert.Equal(outcome.NewMatchId, byCode!.Id);
    }
}
