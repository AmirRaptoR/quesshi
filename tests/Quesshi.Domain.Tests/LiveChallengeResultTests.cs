using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

/// <summary>
/// Pins the enum's ordinals down. Nothing here would actually break if they drifted — every place
/// this crosses a process boundary does so as a bare <c>int</c> (see <c>LobbyHub.Challenge</c>'s own
/// remarks on why), but <c>Quesshi.Web</c> has no reference to <c>Quesshi.Domain</c> and so names a
/// handful of these values again as local constants (<c>MainLayout.razor</c>'s own
/// <c>ResultAccepted</c>/<c>ResultExpired</c>/<c>ResultLobbyFull</c>/<c>ResultLobbyTaken</c>) rather
/// than sharing this type. This test is what would catch the two silently drifting apart if this
/// enum were ever reordered.
/// </summary>
public class LiveChallengeResultTests
{
    [Fact]
    public void Ordinals_match_what_Quesshi_Web_hardcodes_for_the_values_it_branches_on()
    {
        Assert.Equal(0, (int)LiveChallengeResult.Sent);
        Assert.Equal(1, (int)LiveChallengeResult.Accepted);
        Assert.Equal(3, (int)LiveChallengeResult.Expired);
        Assert.Equal(8, (int)LiveChallengeResult.LobbyFull);
        Assert.Equal(9, (int)LiveChallengeResult.LobbyTaken);
    }
}
