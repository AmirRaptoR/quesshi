using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// Today's lobby scenarios (owner is the first seat, Start needs two seated and the owner, seat tiles
/// read host or ready, invite tiles read available, invited or joined, a share link carries the code)
/// adapted to the platform's <see cref="SessionView"/>. Capacity, stepper and take-a-seat scenarios have
/// no counterpart: the platform view carries no capacity and a viewer of it is always seated.
/// </summary>
public class LobbyPresentationTests
{
    [Fact]
    public void The_owner_is_always_the_first_seated_participant()
    {
        var owner = LobbyViews.Parse("amir", ["amir", "sara"]);
        var other = LobbyViews.Parse("sara", ["amir", "sara"]);

        Assert.True(LobbyPresentation.IsOwner(owner));
        Assert.False(LobbyPresentation.IsOwner(other));
    }

    [Fact]
    public void A_non_owner_is_never_told_they_can_start_even_with_enough_players()
    {
        Assert.False(LobbyPresentation.CanStart(LobbyViews.Parse("sara", ["amir", "sara"])));
    }

    [Fact]
    public void The_owner_alone_cannot_start()
    {
        var view = LobbyViews.Parse("amir", ["amir"]);

        Assert.True(LobbyPresentation.IsOwner(view));
        Assert.False(LobbyPresentation.CanStart(view));
    }

    [Fact]
    public void The_owner_with_two_or_more_seated_can_start()
    {
        Assert.True(LobbyPresentation.CanStart(LobbyViews.Parse("amir", ["amir", "sara"])));
        Assert.True(LobbyPresentation.CanStart(LobbyViews.Parse("amir", ["amir", "sara", "vahid"])));
    }

    [Fact]
    public void The_owner_cannot_start_a_session_that_is_no_longer_a_lobby()
    {
        Assert.False(LobbyPresentation.CanStart(LobbyViews.Parse("amir", ["amir", "sara"], status: "running")));
    }

    [Fact]
    public void A_seated_player_is_seated_and_a_stranger_is_not()
    {
        var view = LobbyViews.Parse("amir", ["amir", "sara"]);

        Assert.True(LobbyPresentation.IsSeated(view, "amir"));
        Assert.True(LobbyPresentation.IsSeated(view, "sara"));
        Assert.False(LobbyPresentation.IsSeated(view, "stranger"));
    }

    [Fact]
    public void Seats_come_lowest_seat_first()
    {
        var view = LobbyViews.Parse("amir", ["amir", "sara", "vahid"]);

        Assert.Equal(["amir", "sara", "vahid"], LobbyPresentation.Seats(view).Select(seat => seat.Id));
    }

    [Fact]
    public void The_owners_own_seat_tile_reads_host_and_anyone_else_reads_ready()
    {
        var view = LobbyViews.Parse("amir", ["amir", "sara"]);
        var seats = LobbyPresentation.Seats(view);

        Assert.Equal(LobbyPresentation.SeatState.Host, LobbyPresentation.SeatStateFor(view, seats[0]));
        Assert.Equal(LobbyPresentation.SeatState.Ready, LobbyPresentation.SeatStateFor(view, seats[1]));
    }

    [Fact]
    public void An_invite_tile_is_available_for_someone_not_yet_touched()
    {
        var view = LobbyViews.Parse("amir", ["amir"]);

        Assert.Equal(LobbyPresentation.InviteTileState.Available,
            LobbyPresentation.InviteState(view, new LobbyInvitee("cam", "Cam"), alreadyInvited: false));
    }

    [Fact]
    public void An_invite_tile_is_invited_once_this_visit_has_sent_one()
    {
        var view = LobbyViews.Parse("amir", ["amir"]);

        Assert.Equal(LobbyPresentation.InviteTileState.Invited,
            LobbyPresentation.InviteState(view, new LobbyInvitee("cam", "Cam"), alreadyInvited: true));
    }

    [Fact]
    public void An_invite_tile_is_joined_once_the_invitee_is_seated_even_if_invited_by_this_visit()
    {
        var view = LobbyViews.Parse("amir", ["amir", "cam"]);

        Assert.Equal(LobbyPresentation.InviteTileState.Joined,
            LobbyPresentation.InviteState(view, new LobbyInvitee("cam", "Cam"), alreadyInvited: true));
    }

    [Fact]
    public void The_share_link_contains_the_lobbys_join_code()
    {
        var view = LobbyViews.Parse("amir", ["amir"], code: "ZK7Q4M");

        var link = LobbyPresentation.ShareLink("https://tenant.example/", view);

        Assert.Equal("https://tenant.example/join/ZK7Q4M", link);
    }
}
