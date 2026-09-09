namespace Quesshi.Web.Services;

/// <summary>Which of the two existing invitation paths a friend row's "Duel" pill sends. See #91.</summary>
public enum FriendDuelKind { Live, Async }

/// <summary>
/// Profile.razor's Duel pill carries no menu — one tap, and which of the two existing invitation
/// paths it takes is decided by nothing but the friend's live presence (<c>FriendDto.Online</c>).
/// A friend connected to <c>LobbyHub</c> right now gets the instant live challenge
/// (<c>LobbyClient.ChallengeAsync</c>, unchanged from before this issue). Everyone else — offline, or
/// a guest, whose <c>Online</c> is always false because a guest connection to <c>LobbyHub</c> is
/// refused outright in <c>OnConnectedAsync</c> — gets an async duel instead, with its code copied to
/// the clipboard rather than delivered in-app: <c>LobbyHub.InviteToLobby</c> always resolves its
/// <c>lobbyId</c> through <c>ILiveMatchGrain</c> (see that method's own remarks), so it can never point
/// at an async lobby (those live in <c>IMatchGrain</c> instead) — there is no direct async-invite path
/// to reach for here, only the duel's own share code, which is exactly how a guest friend is reached
/// today regardless of this pill.
///
/// Pulled out as a pure function of one bool — rather than inlined in Profile.razor's click handler —
/// so the choice itself is provable with a plain xunit test and no rendered page or live connection,
/// the same reasoning <c>InviteFriendPlan</c> and <c>LobbyPresentation</c> already followed for their
/// own branch points.
/// </summary>
public static class FriendDuelPlan
{
    public static FriendDuelKind Choose(bool online) => online ? FriendDuelKind.Live : FriendDuelKind.Async;
}
