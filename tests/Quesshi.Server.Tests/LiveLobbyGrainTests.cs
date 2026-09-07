using System.Diagnostics;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public class LiveLobbyGrainTests(LiveClusterFixture fixture)
{
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

    private static void Advance(TimeSpan by) => LiveShared.TimeProvider.Advance(by);

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

        Assert.Equal((int)LiveChallengeResult.Sent, await lobby.EnqueueAsync(queued, (int)Language.En));

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

        Assert.Equal((int)LiveChallengeResult.CallerCommitted, await lobby.EnqueueAsync(challenger, (int)Language.En));
        Assert.Equal((int)LiveChallengeResult.CallerCommitted, await lobby.EnqueueAsync(target, (int)Language.En));
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

        // Both are free again.
        Assert.Equal((int)LiveChallengeResult.Sent, await lobby.EnqueueAsync(challenger, (int)Language.En));
    }
}
