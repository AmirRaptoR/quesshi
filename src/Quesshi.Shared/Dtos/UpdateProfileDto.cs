namespace Quesshi.Shared;

/// <summary>
/// <see cref="AvatarSeed"/> is issue #54's contract addition (spec section 4), landed here alongside
/// the rest of that issue: <c>PUT /api/me</c> validates a non-null seed against
/// <see cref="AvatarPalette"/> and, if it passes, calls <c>Player.SetAvatar</c> through
/// <c>IPlayerGrain.UpdateProfileAsync</c> — the same grain-owned write path the rename and language
/// change now go through too, so a match settling moments later can never undo any of the three. Null
/// leaves the avatar untouched, the same "nothing to change" convention as every optional field on
/// this API. <c>PUT /api/me</c> also now carries <c>AllowGuest</c>, so this DTO reaches a guest's own
/// identity edit exactly as it does a signed-in player's.
/// </summary>
public sealed record UpdateProfileDto(string DisplayName, string Lang, string? AvatarSeed = null);
