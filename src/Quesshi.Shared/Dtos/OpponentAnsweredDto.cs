namespace Quesshi.Shared;

/// <summary>
/// The #13 contract addition: fired once, on the first answer of a round, so a client can show
/// "they have answered" without polling. Carries the round slot and who answered — no choice
/// index, no score; the reveal that follows is what's authoritative for both.
/// </summary>
public sealed record OpponentAnsweredDto(string MatchId, int Slot, string PlayerId);
