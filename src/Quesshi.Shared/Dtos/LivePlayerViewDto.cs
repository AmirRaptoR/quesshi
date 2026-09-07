namespace Quesshi.Shared;

/// <summary>One player's running total. Score and Correct count revealed rounds only.</summary>
public sealed record LivePlayerViewDto(string PlayerId, int Score, int Correct, int MissStreak);
