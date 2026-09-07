namespace Quesshi.Shared;

/// <summary>
/// The live twin of <see cref="GuestResultDto"/>: becoming a guest and taking the lobby seat are one
/// step, so a half-made guest with no duel to play cannot exist.
/// </summary>
public sealed record GuestLiveResultDto(string Token, MeDto Me, LiveViewDto Live);
