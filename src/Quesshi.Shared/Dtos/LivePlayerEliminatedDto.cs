namespace Quesshi.Shared;

/// <summary>
/// Pushed once for whoever <c>CloseRound</c> just dropped to the miss-streak. The eliminated
/// player's own client is what switches to spectating on this; everyone else's is what stops
/// waiting on that seat.
/// </summary>
public sealed record LivePlayerEliminatedDto(string PlayerId, int RoundSlot);
