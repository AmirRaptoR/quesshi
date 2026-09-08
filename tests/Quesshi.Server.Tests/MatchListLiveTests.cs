using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>
/// A live duel in the match list: it has no grain to ask (that is issue #10), so it is built straight
/// from the archive row <c>LiveMatchSettlement</c> mirrors on start and on end — but it still has to
/// show up beside async duels, still marked live, and never as one this player can just "Play".
/// </summary>
[Collection(nameof(ClusterCollection))]
public class MatchListLiveTests(ClusterFixture fixture)
{
    private IGrainFactory Grains => fixture.Cluster.GrainFactory;

    [Fact]
    public async Task A_live_duel_appears_in_the_list_marked_live_and_not_playable()
    {
        const string me = "p-livelist-me";
        const string rival = "p-livelist-rival";
        await Shared.Players.UpsertAsync(Player.Register(me, $"{me}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(rival, $"{rival}@example.com", "Sara", Language.En, Shared.Clock.Now));

        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "livelist-1", "livelist-1", Language.En, me, rival, WinnerId: null, IsDraw: false,
            Results: FakeArchive.TestResults(me, rival, 0, 0), MatchState.InProgress, Shared.Clock.Now, EndedAt: null,
            QuestionIds: [], IsLive: true));

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: null, Shared.Archive, Shared.Players, Grains);

        var row = list.Single(m => m.Id == "livelist-1");
        Assert.True(row.IsLive);
        Assert.False(row.CanPlay);
        Assert.Equal("Sara", row.Opponent!.DisplayName);
    }

    [Fact]
    public async Task A_live_duel_never_pushes_a_playable_async_duel_out_of_a_capped_list()
    {
        const string me = "p-livecap-me";
        const string rival = "p-livecap-rival";
        await Shared.Players.UpsertAsync(Player.Register(me, $"{me}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(rival, $"{rival}@example.com", "Sara", Language.En, Shared.Clock.Now));

        // One playable async duel, waiting on this player.
        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"livecap-q{slot}";
            Shared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                "q", ["right", "w1", "w2", "w3"], 0, Shared.Clock.Now, status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        var asyncGrain = Grains.GetGrain<IMatchGrain>("livecap-async");
        await asyncGrain.CreateAsync((int)Language.En, me, ids, "LIVECAP");
        await asyncGrain.JoinAsync(rival);

        // A live duel, newer than the async one, but never playable.
        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "livecap-live", "livecap-live", Language.En, me, rival, WinnerId: null, IsDraw: false,
            Results: FakeArchive.TestResults(me, rival, 0, 0), MatchState.InProgress,
            Shared.Clock.Now.AddMinutes(1), EndedAt: null, QuestionIds: [], IsLive: true));

        var one = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: 1, Shared.Archive, Shared.Players, Grains);

        Assert.Single(one);
        Assert.Equal("livecap-async", one[0].Id);
        Assert.True(one[0].CanPlay);
    }

    [Fact]
    public async Task Building_the_list_activates_no_grain_for_a_live_row()
    {
        const string me = "p-livenograin-me";
        const string rival = "p-livenograin-rival";
        await Shared.Players.UpsertAsync(Player.Register(me, $"{me}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(rival, $"{rival}@example.com", "Sara", Language.En, Shared.Clock.Now));

        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "livenograin-1", "livenograin-1", Language.En, me, rival, WinnerId: null, IsDraw: false,
            Results: FakeArchive.TestResults(me, rival, 3, 1), MatchState.InProgress, Shared.Clock.Now, EndedAt: null,
            QuestionIds: [], IsLive: true));

        var spy = GrainActivationSpy.Wrap(Grains, out var requests);

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: null, Shared.Archive, Shared.Players, spy);

        Assert.Contains(list, m => m.Id == "livenograin-1");
        Assert.DoesNotContain(requests, r => r.GrainInterface == typeof(IMatchGrain) && r.Key == "livenograin-1");
    }

    [Fact]
    public async Task A_no_contest_live_duel_is_left_out_of_the_list_entirely()
    {
        const string me = "p-livenc-me";
        const string rival = "p-livenc-rival";
        await Shared.Players.UpsertAsync(Player.Register(me, $"{me}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(rival, $"{rival}@example.com", "Sara", Language.En, Shared.Clock.Now));

        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "livenc-1", "livenc-1", Language.En, me, rival, WinnerId: null, IsDraw: false,
            Results: FakeArchive.TestResults(me, rival, 0, 0), MatchState.NoContest, Shared.Clock.Now, EndedAt: Shared.Clock.Now,
            QuestionIds: [], IsLive: true));

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: null, Shared.Archive, Shared.Players, Grains);

        Assert.DoesNotContain(list, m => m.Id == "livenc-1");
    }

    [Fact]
    public async Task An_awaiting_opponent_live_lobby_appears_with_a_null_opponent_and_is_not_playable()
    {
        const string me = "p-livelobby-me";
        await Shared.Players.UpsertAsync(Player.Register(me, $"{me}@example.com", "Amir", Language.En, Shared.Clock.Now));

        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "livelobby-1", "livelobby-1", Language.En, me, OpponentId: null, WinnerId: null, IsDraw: false,
            Results: FakeArchive.TestResults(me, null, 0, 0), MatchState.AwaitingOpponent, Shared.Clock.Now, EndedAt: null,
            QuestionIds: [], IsLive: true));

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: null, Shared.Archive, Shared.Players, Grains);

        var row = list.Single(m => m.Id == "livelobby-1");
        Assert.Null(row.Opponent);
        Assert.False(row.CanPlay);
    }

    [Fact]
    public async Task Active_only_keeps_a_live_in_progress_row_and_drops_a_live_resolved_one()
    {
        const string me = "p-liveactive-me";
        const string rival = "p-liveactive-rival";
        await Shared.Players.UpsertAsync(Player.Register(me, $"{me}@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(rival, $"{rival}@example.com", "Sara", Language.En, Shared.Clock.Now));

        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "liveactive-inprogress", "liveactive-inprogress", Language.En, me, rival, WinnerId: null, IsDraw: false,
            Results: FakeArchive.TestResults(me, rival, 0, 0), MatchState.InProgress, Shared.Clock.Now, EndedAt: null,
            QuestionIds: [], IsLive: true));
        await Shared.Archive.SaveAsync(new ArchivedMatch(
            "liveactive-resolved", "liveactive-resolved", Language.En, me, rival, WinnerId: me, IsDraw: false,
            Results: FakeArchive.TestResults(me, rival, 5, 2), MatchState.Resolved, Shared.Clock.Now, EndedAt: Shared.Clock.Now,
            QuestionIds: [], IsLive: true));

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: true, take: null, Shared.Archive, Shared.Players, Grains);

        Assert.Contains(list, m => m.Id == "liveactive-inprogress");
        Assert.DoesNotContain(list, m => m.Id == "liveactive-resolved");
    }
}
