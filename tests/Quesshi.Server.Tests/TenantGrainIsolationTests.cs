using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class TenantGrainIsolationTests(ClusterFixture fixture)
{
    [Fact]
    public async Task Integer_grain_state_is_separate_for_the_same_key_in_two_tenants()
    {
        var tenant = new TenantContext();
        var id = Random.Shared.NextInt64(10, 1_000_000);
        var quesshi = fixture.Cluster.GrainFactory.GetTenantGrain<ILobbySettingsGrain>(id);

        Assert.True(await quesshi.SetMaxCapacityAsync(6));

        using (tenant.Enter("brand-a"))
        {
            var brandA = fixture.Cluster.GrainFactory.GetTenantGrain<ILobbySettingsGrain>(id);
            Assert.True(await brandA.SetMaxCapacityAsync(7));
            Assert.Equal(7, await brandA.GetMaxCapacityAsync());
        }

        Assert.Equal(6, await quesshi.GetMaxCapacityAsync());
    }
}
