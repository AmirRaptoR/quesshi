using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>Issue #91's Duel pill: one tap, and which of the two existing invitation paths it takes
/// is decided by nothing but the friend's live presence. See FriendDuelPlan's own remarks for why an
/// offline friend — a guest included, since a guest's Online is always false — gets the async path.</summary>
public class FriendDuelPlanTests
{
    [Fact]
    public void An_online_friend_gets_the_instant_live_challenge()
        => Assert.Equal(FriendDuelKind.Live, FriendDuelPlan.Choose(online: true));

    [Fact]
    public void An_offline_friend_gets_an_async_duel_instead()
        => Assert.Equal(FriendDuelKind.Async, FriendDuelPlan.Choose(online: false));
}
