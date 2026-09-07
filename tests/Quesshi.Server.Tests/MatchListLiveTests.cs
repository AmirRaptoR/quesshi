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
            ChallengerScore: 0, OpponentScore: 0, MatchState.InProgress, Shared.Clock.Now, EndedAt: null,
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
            ChallengerScore: 0, OpponentScore: 0, MatchState.InProgress,
            Shared.Clock.Now.AddMinutes(1), EndedAt: null, QuestionIds: [], IsLive: true));

        var one = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: 1, Shared.Archive, Shared.Players, Grains);

        Assert.Single(one);
        Assert.Equal("livecap-async", one[0].Id);
        Assert.True(one[0].CanPlay);
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
            ChallengerScore: 0, OpponentScore: 0, MatchState.NoContest, Shared.Clock.Now, EndedAt: Shared.Clock.Now,
            QuestionIds: [], IsLive: true));

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, take: null, Shared.Archive, Shared.Players, Grains);

        Assert.DoesNotContain(list, m => m.Id == "livenc-1");
    }
}
