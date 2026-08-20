namespace Quesshi.Shared;

/// <summary>
/// Becoming a guest and taking the seat are one step, so a half-made guest with no match to play
/// cannot exist.
/// </summary>
public sealed record GuestResultDto(string Token, MeDto Me, MatchSummaryDto Match);
