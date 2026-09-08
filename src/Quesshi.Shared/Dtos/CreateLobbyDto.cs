namespace Quesshi.Shared;

/// <summary>
/// What the owner picks when opening an N-player lobby. Unlike <see cref="CreateMatchDto"/>, no
/// question set is built here — that happens at Start, from whatever <c>DuelSettings</c> this becomes
/// says at that instant — so this carries the settings themselves plus <see cref="Capacity"/>, the one
/// field a lobby needs that a settings object does not. Null categories means "surprise me", null
/// levels means the full ramp, null questions means the default length, exactly as
/// <see cref="CreateMatchDto"/>'s own remarks say; all of it is validated server-side because this
/// arrives from a browser.
/// </summary>
public sealed record CreateLobbyDto(int Capacity, string? Lang = null,
    List<string>? Categories = null, int? Questions = null, List<int>? Levels = null);
