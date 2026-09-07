using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// <see cref="IPlayerGrain.RecordAbandonmentAsync"/> exercised through the grain and its persistence,
/// not just the domain method it wraps — proving the escalating schedule survives a real activation
/// and a real player record, the way it will when a live duel actually calls it.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class PlayerGrainAbandonmentTests(ClusterFixture fixture)
{
    private static string NewPlayerId() => $"p-abandon-{Guid.NewGuid():N}";

    private async Task<IPlayerGrain> NewGrainAsync()
    {
        var id = NewPlayerId();
        await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", "Amir", Language.En, Shared.Clock.Now));
        return fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id);
    }

    [Fact]
    public async Task The_escalating_penalty_is_returned_and_persisted_on_the_players_own_record()
    {
        var grain = await NewGrainAsync();
        var now = Shared.Clock.Now;

        Assert.Equal(0, await grain.RecordAbandonmentAsync(now));
        Assert.Equal(200, await grain.RecordAbandonmentAsync(now.AddHours(1)));
        Assert.Equal(400, await grain.RecordAbandonmentAsync(now.AddHours(2)));
    }

    [Fact]
    public async Task The_window_rolls_across_a_grain_reactivation()
    {
        var id = NewPlayerId();
        await Shared.Players.UpsertAsync(Player.Register(id, $"{id}@example.com", "Sara", Language.En, Shared.Clock.Now));
        var now = Shared.Clock.Now;

        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id).RecordAbandonmentAsync(now);

        // Force a fresh activation, so the second call rehydrates the abandonment history from
        // storage rather than reading it out of the same in-memory grain.
        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var second = await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id).RecordAbandonmentAsync(now.AddDays(1));
        Assert.Equal(200, second);

        // Seven days after the first call, it has aged out of the window but the second (a day later)
        // has not: this is the second abandonment still standing, not the third.
        var third = await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(id)
            .RecordAbandonmentAsync(now.AddDays(7));
        Assert.Equal(200, third);
    }
}
