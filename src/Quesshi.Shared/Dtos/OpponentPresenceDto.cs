namespace Quesshi.Shared;

/// <summary>Cosmetic only. A missing socket never means the duel paused — silence, not presence, decides abandonment.</summary>
public sealed record OpponentPresenceDto(string PlayerId, bool Online);
