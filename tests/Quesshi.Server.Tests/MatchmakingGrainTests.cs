using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public class MatchmakingGrainTests(ClusterFixture fixture)
{
    [Fact]
    public async Task The_second_player_to_ask_is_handed_the_first_players_match()
    {
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchmakingGrain>(Random.Shared.NextInt64(1000, 99999));

        Assert.Null(await queue.FindOrQueueAsync("p-one", (int)Language.En, "match-one"));
        Assert.Equal("match-one", await queue.FindOrQueueAsync("p-two", (int)Language.En, "match-two"));
        Assert.Equal(0, await queue.WaitingCountAsync());
    }

    [Fact]
    public async Task Players_waiting_in_different_languages_are_not_paired()
    {
        var queue = fixture.Cluster.GrainFactory.GetGrain<IMatchmakingGrain>(Random.Shared.NextInt64(100000, 999999));

        Assert.Null(await queue.FindOrQueueAsync("p-fa", (int)Language.Fa, "match-fa"));
        Assert.Null(await queue.FindOrQueueAsync("p-en", (int)Language.En, "match-en"));
        Assert.Equal(2, await queue.WaitingCountAsync());
    }
}
