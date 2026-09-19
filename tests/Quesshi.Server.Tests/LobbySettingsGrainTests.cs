using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public sealed class LobbySettingsGrainTests(LiveClusterFixture fixture)
{
    private static long _n = 20_000;
    private ILobbySettingsGrain NewGrain() => fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(Interlocked.Increment(ref _n));

    [Fact]
    public async Task Defaults_to_twenty_and_validates_the_absolute_range()
    {
        var grain = NewGrain();
        Assert.Equal(20, await grain.GetMaxCapacityAsync());
        Assert.False(await grain.SetMaxCapacityAsync(1));
        Assert.False(await grain.SetMaxCapacityAsync(501));
        Assert.Equal(20, await grain.GetMaxCapacityAsync());
        Assert.True(await grain.SetMaxCapacityAsync(500));
        Assert.Equal(500, await grain.GetMaxCapacityAsync());
    }

    [Fact]
    public async Task Seeding_never_overwrites_an_admin_value()
    {
        var grain = NewGrain();
        await grain.SeedAsync(40);
        Assert.Equal(40, await grain.GetMaxCapacityAsync());
        Assert.True(await grain.SetMaxCapacityAsync(100));
        await grain.SeedAsync(20);
        Assert.Equal(100, await grain.GetMaxCapacityAsync());
    }

    [Fact]
    public async Task Saved_value_survives_grain_reactivation()
    {
        var key = Interlocked.Increment(ref _n);
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(key);
        Assert.True(await grain.SetMaxCapacityAsync(123));

        await grain.AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        Assert.Equal(123, await fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(key).GetMaxCapacityAsync());
    }
}
