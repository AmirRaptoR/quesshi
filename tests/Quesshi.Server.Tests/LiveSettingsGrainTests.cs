using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public class LiveSettingsGrainTests(LiveClusterFixture fixture)
{
    private static long _n = 1000;

    // Each test gets its own grain key, since key 0 is where Program.cs's real seed startup task
    // points and this suite's tests must not see each other's writes on a cluster shared per class.
    private ILiveSettingsGrain NewGrain() => fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(Interlocked.Increment(ref _n));

    [Fact]
    public async Task Defaults_to_enabled_before_anything_is_ever_seeded_or_set()
    {
        var grain = NewGrain();
        Assert.True(await grain.IsEnabledAsync());
    }

    [Fact]
    public async Task Seeding_after_a_runtime_toggle_does_not_overwrite_it()
    {
        var grain = NewGrain();

        await grain.SeedAsync(false);
        Assert.False(await grain.IsEnabledAsync());

        await grain.SetEnabledAsync(true);
        Assert.True(await grain.IsEnabledAsync());

        // A restart re-runs the seed with the same configured default; it must not clobber the toggle.
        await grain.SeedAsync(false);
        Assert.True(await grain.IsEnabledAsync());
    }

    [Fact]
    public async Task Toggling_takes_effect_immediately_for_the_next_read()
    {
        var grain = NewGrain();

        await grain.SetEnabledAsync(false);
        Assert.False(await grain.IsEnabledAsync());

        await grain.SetEnabledAsync(true);
        Assert.True(await grain.IsEnabledAsync());
    }
}
