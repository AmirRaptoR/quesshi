namespace Quesshi.Domain;

/// <summary>
/// One player leaving a live duel to the miss-streak, and which round they were in when it happened.
/// The round slot is what standings rank abandoners by — lasting longer places better — which is
/// exactly what a plain set of abandoned player ids cannot encode. Recorded in the order
/// <c>CloseRound</c> discovers them, so several players hitting the streak in the same round share a
/// <see cref="RoundSlot"/> and, in <see cref="LiveMatch.Standings"/>, share a place.
/// </summary>
public sealed record Abandonment(string PlayerId, int RoundSlot);
