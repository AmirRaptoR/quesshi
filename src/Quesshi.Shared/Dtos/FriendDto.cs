namespace Quesshi.Shared;

/// <summary>
/// <paramref name="Online"/> defaults to false so the <c>/api/players/search</c> call site — a
/// stranger in a search result is not challengeable, so it is never marked — stays unchanged.
/// <paramref name="IsGuest"/> (issue #53's lobby invite) is what lets <c>Lobby.razor</c>'s
/// invite-a-friend control tell a guest friend apart from a real one without a round trip of its
/// own: a guest can never receive an in-app invitation (see <c>LobbyHub.OnConnectedAsync</c>'s own
/// remarks), so that control shows "link only" for one instead of a button that could never work.
/// </summary>
public sealed record FriendDto(string Id, string DisplayName, string AvatarSeed, long Score, bool Online = false, bool IsGuest = false);
