namespace Quesshi.Shared;

/// <summary>
/// <see cref="AvatarSeed"/> is issue #54's contract addition (spec section 4): built against here
/// ahead of that issue landing on <c>main</c>, since the lobby page (issue #53) is the one place a
/// guest ever gets to edit their identity and needed somewhere to send the picked seed. Until #54
/// adds <c>Player.SetAvatar</c> and validates this server-side, the field is accepted but ignored —
/// a guest's <c>PUT /api/me</c> also still 403s until that issue's <c>AllowGuest</c> metadata lands.
/// Null leaves the avatar untouched, the same "nothing to change" convention as every optional field
/// on this API.
/// </summary>
public sealed record UpdateProfileDto(string DisplayName, string Lang, string? AvatarSeed = null);
