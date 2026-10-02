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

    [Fact]
    public async Task String_grain_state_is_separate_and_quesshi_cannot_forge_a_tenant_prefix()
    {
        var id = $"tenant-isolation-player-{Guid.NewGuid():N}";
        await Shared.Players.UpsertAsync(Quesshi.Domain.Player.Register(id, "legacy@example.test", "Legacy", Quesshi.Domain.Language.En, Shared.Clock.Now));
        using (Shared.Tenant.Enter("brand-a"))
            await Shared.Players.UpsertAsync(Quesshi.Domain.Player.Register(id, "brand-a@example.test", "Brand A", Quesshi.Domain.Language.En, Shared.Clock.Now));

        var quesshi = fixture.Cluster.GrainFactory.GetTenantGrain<IPlayerGrain>(id);
        await quesshi.AddFriendAsync("legacy-friend");
        using (Shared.Tenant.Enter("brand-a"))
        {
            var brandA = fixture.Cluster.GrainFactory.GetTenantGrain<IPlayerGrain>(id);
            await brandA.AddFriendAsync("brand-a-friend");
            Assert.Equal("Brand A", (await Shared.Players.GetAsync(id))!.DisplayName);
            Assert.Equal(["brand-a-friend"], (await Shared.Players.GetAsync(id))!.Friends);
        }

        Assert.Equal("Legacy", (await Shared.Players.GetAsync(id))!.DisplayName);
        Assert.Equal(["legacy-friend"], (await Shared.Players.GetAsync(id))!.Friends);
        Assert.Throws<ArgumentException>(() => fixture.Cluster.GrainFactory.GetTenantGrain<IPlayerGrain>("quessher:victim"));
    }
}
