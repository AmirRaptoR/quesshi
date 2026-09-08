namespace Quesshi.Application.Ports;

/// <summary>
/// One player dropping out of a live duel to the miss-streak, at the round they dropped in. Pushed
/// once, the instant <c>LiveMatch.CloseRound</c> adds them to <c>Abandoners</c> — which is the only
/// place a player is ever added to that set, so this is the entire elimination wire contract: without
/// it, nothing tells an eliminated client to stop offering answer buttons, and nothing tells the
/// survivors to stop waiting on a seat that will never fill again. <see cref="RoundSlot"/> mirrors
/// <c>Abandonment.RoundSlot</c> — the round this player was in when their third consecutive miss
/// landed, not necessarily the round current when this push goes out (a reactivation catching up on
/// several missed rounds at once can raise more than one of these back to back).
/// </summary>
public sealed record LivePlayerEliminated(string PlayerId, int RoundSlot);
