using Quesshi.Shared;

namespace Quesshi.Web.Plugins.Features.Leaderboard;

/// <summary>Registers what the leaderboard plugin contributes to the player shell.</summary>
public static class LeaderboardClient
{
    public const string RanksPath = "/ranks";

    public static void Register(IFormatClientRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterPage(RanksPath, typeof(LeaderboardPage));
        registry.RegisterSlot(ClientSlots.NavItem, typeof(LeaderboardNavItem));
        registry.RegisterSlot(ClientSlots.HomeTile, typeof(LeaderboardHomeTile));
        registry.RegisterSlot(ClientSlots.SessionResult, typeof(LeaderboardSessionResult));
    }
}
