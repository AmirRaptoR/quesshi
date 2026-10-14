using Microsoft.AspNetCore.DataProtection;
using Quesshi.Engine.Contracts;
using Quesshi.Server.Security;

namespace Quesshi.Server.Tests;

public sealed class PlayerAntiforgeryTests
{
    [Fact]
    public void Token_is_bound_to_its_tenant_and_current_identity()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var antiforgery = new PlayerAntiforgery(new EphemeralDataProtectionProvider(), clock);
        var tenant = Guid.NewGuid();
        var player = new PlayerIdentity(tenant, Guid.NewGuid(), Guid.NewGuid(), false);

        var anonymousToken = antiforgery.Issue(tenant, null);
        var playerToken = antiforgery.Issue(tenant, player);

        Assert.True(antiforgery.IsValid(tenant, null, anonymousToken));
        Assert.False(antiforgery.IsValid(tenant, player, anonymousToken));
        Assert.True(antiforgery.IsValid(tenant, player, playerToken));
        Assert.False(antiforgery.IsValid(Guid.NewGuid(), player, playerToken));

        clock.Advance(TimeSpan.FromDays(2));
        Assert.False(antiforgery.IsValid(tenant, player, playerToken));
    }
}
