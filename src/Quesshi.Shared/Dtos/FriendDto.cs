namespace Quesshi.Shared;

/// <summary>
/// <paramref name="Online"/> defaults to false so the <c>/api/players/search</c> call site — a
/// stranger in a search result is not challengeable, so it is never marked — stays unchanged.
/// </summary>
public sealed record FriendDto(string Id, string DisplayName, string AvatarSeed, long Score, bool Online = false);
