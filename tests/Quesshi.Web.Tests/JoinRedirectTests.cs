using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public class JoinRedirectTests
{
    private static InviteDto Invite(string matchId, bool live = false) =>
        new("CODE", matchId, "Challenger", "avatar", 10, true, live);

    [Fact]
    public void A_pinned_guest_reopening_their_own_async_code_goes_straight_to_duel()
        => Assert.Equal("/duel/match-a", JoinRedirect.TargetFor(isGuest: true, guestMatchId: "match-a", invite: Invite("match-a")));

    [Fact]
    public void A_pinned_guest_reopening_their_own_live_code_goes_straight_to_live()
        => Assert.Equal("/live/match-a", JoinRedirect.TargetFor(isGuest: true, guestMatchId: "match-a", invite: Invite("match-a", live: true)));

    [Fact]
    public void A_pinned_guest_opening_a_different_matchs_code_is_not_redirected()
        => Assert.Null(JoinRedirect.TargetFor(isGuest: true, guestMatchId: "match-a", invite: Invite("match-b")));

    [Fact]
    public void A_guest_with_no_pin_is_not_redirected()
        => Assert.Null(JoinRedirect.TargetFor(isGuest: true, guestMatchId: null, invite: Invite("match-b")));

    [Fact]
    public void A_signed_in_non_guest_is_never_redirected_even_with_a_pin()
        => Assert.Null(JoinRedirect.TargetFor(isGuest: false, guestMatchId: "match-a", invite: Invite("match-a")));

    [Fact]
    public void An_anonymous_visitor_is_never_redirected()
        => Assert.Null(JoinRedirect.TargetFor(isGuest: false, guestMatchId: null, invite: Invite("match-a")));

    [Fact]
    public void An_unresolved_invite_never_redirects_a_pinned_guest()
        => Assert.Null(JoinRedirect.TargetFor(isGuest: true, guestMatchId: "match-a", invite: null));
}
