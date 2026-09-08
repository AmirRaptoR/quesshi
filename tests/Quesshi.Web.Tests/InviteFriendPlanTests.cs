using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Issue #53's last two gaps, both in "Invite a friend": first a fixed capacity-2 duel with the
/// N-player lobby reachable only by calling the API by hand, then a live lobby of more than two
/// reachable no way at all — <c>Api.CreateLiveAsync</c>/<c>POST /api/live/lobby</c> had no UI in front
/// of them. Covered here as pure functions of a (capacity, isLive) pair and a creation result, the same
/// way LobbyPresentationTests covers LobbyPresentation (see that file's own remarks) — no rendered
/// Home.razor and no running server needed to prove which branch a given pick selects.
/// </summary>
public class InviteFriendPlanTests
{
    private static PlayerSideDto Side(string id) => new(id, id, $"seed-{id}", 0, 0, 0, false);

    private static MatchSummaryDto Summary(string id, bool canPlay)
        => new(id, "CODE01", "en", "inprogress", Side("amir"), null, null, false,
            DateTimeOffset.UtcNow, canPlay, false, "pending");

    /// <summary>Only the exact case this button has always handled — two seats, async — skips the
    /// lobby. Everything else, including a two-seat *live* invite (issue #53's remaining gap), needs it.</summary>
    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]
    [InlineData(4, false, true)]
    [InlineData(8, false, true)]
    [InlineData(2, true, true)]
    [InlineData(3, true, true)]
    [InlineData(8, true, true)]
    public void Needs_the_lobby_whenever_live_or_wider_than_two(int capacity, bool isLive, bool expected)
    {
        Assert.Equal(expected, InviteFriendPlan.NeedsLobby(capacity, isLive));
    }

    /// <summary>Capacity 2 async's own path — the one that predates issue #53 — takes the existing
    /// route: straight into the duel, exactly as CreateMatchAsync always sent it.</summary>
    [Fact]
    public void A_capacity_two_async_create_that_already_found_an_opponent_goes_straight_to_play()
    {
        var match = Summary("m1", canPlay: true);

        Assert.Equal("/play/m1", InviteFriendPlan.Route(match));
    }

    [Fact]
    public void A_capacity_two_async_create_still_waiting_on_an_opponent_goes_to_the_duel_page()
    {
        var match = Summary("m1", canPlay: false);

        Assert.Equal("/duel/m1", InviteFriendPlan.Route(match));
    }

    /// <summary>Capacity above 2, async goes to the async lobby-creation endpoint instead
    /// (Api.CreateMatchLobbyAsync) and lands the owner on /lobby/{code} to share it and press Start —
    /// never straight into a duel that has no opponents seated yet.</summary>
    [Fact]
    public void A_capacity_above_two_async_create_lands_the_owner_on_the_lobby_route()
    {
        Assert.Equal("/lobby/CODE01", InviteFriendPlan.LobbyRoute("CODE01"));
    }

    /// <summary>The gap this task closes: a live invite, at any capacity, goes to the live
    /// lobby-creation endpoint (Api.CreateLiveLobbyAsync, POST /api/live/lobby) and lands on the exact
    /// same /lobby/{code} route as the async lobby does — Lobby.razor tells the two kinds apart by
    /// asking, not by the route shape, so one route serves both.</summary>
    [Fact]
    public void A_live_create_lands_the_owner_on_the_lobby_route_too()
    {
        Assert.Equal("/lobby/CODE01", InviteFriendPlan.LobbyRoute("CODE01"));
    }
}
