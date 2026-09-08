using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Issue #53's last gap: "Invite a friend" only ever created a fixed capacity-2 duel, with the
/// N-player lobby it now offers reachable only by calling the API by hand. Covered here as pure
/// functions of a capacity and a creation result, the same way LobbyPresentationTests covers
/// LobbyPresentation (see that file's own remarks) — no rendered Home.razor and no running server
/// needed to prove which branch capacity selects.
/// </summary>
public class InviteFriendPlanTests
{
    private static PlayerSideDto Side(string id) => new(id, id, $"seed-{id}", 0, 0, 0, false);

    private static MatchSummaryDto Summary(string id, bool canPlay)
        => new(id, "CODE01", "en", "inprogress", Side("amir"), null, null, false,
            DateTimeOffset.UtcNow, canPlay, false, "pending");

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void Only_capacity_above_two_needs_the_lobby(int capacity, bool expected)
    {
        Assert.Equal(expected, InviteFriendPlan.NeedsLobby(capacity));
    }

    /// <summary>Capacity 2's own path — the one that predates issue #53 — takes the existing route:
    /// straight into the duel, exactly as CreateMatchAsync always sent it.</summary>
    [Fact]
    public void A_capacity_two_create_that_already_found_an_opponent_goes_straight_to_play()
    {
        var match = Summary("m1", canPlay: true);

        Assert.Equal("/play/m1", InviteFriendPlan.Route(match));
    }

    [Fact]
    public void A_capacity_two_create_still_waiting_on_an_opponent_goes_to_the_duel_page()
    {
        var match = Summary("m1", canPlay: false);

        Assert.Equal("/duel/m1", InviteFriendPlan.Route(match));
    }

    /// <summary>Capacity above 2 goes to the lobby-creation endpoint instead (Api.CreateMatchLobbyAsync)
    /// and always lands the owner on /lobby/{code} to share it and press Start — never straight into
    /// a duel that has no opponents seated yet.</summary>
    [Fact]
    public void A_capacity_above_two_create_lands_the_owner_on_the_lobby_route()
    {
        Assert.Equal("/lobby/CODE01", InviteFriendPlan.LobbyRoute("CODE01"));
    }
}
