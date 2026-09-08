namespace Quesshi.Domain;

/// <summary>
/// One participant's placement once a duel is over: what they scored, where that placed them, and
/// what it means for them personally. This is what every per-player outcome — settlement, the
/// results screen, the duels list — has to read; the match's own <c>WinnerId</c>/<c>IsDraw</c>
/// summarise the top place alone and cannot carry a whole roster's worth of outcomes for N players.
/// </summary>
/// <param name="Score">
/// What this player banked — zero for an abandoner regardless of what they scored before quitting.
/// Otherwise the winning move in a three-player duel would be to build a lead and then walk away.
/// </param>
/// <param name="Place">
/// 1 is first. Ties share a place, and the place after a tie skips ahead by however many shared it
/// (competition ranking) — two players tied for first and one below them are placed 1, 1, 3.
/// </param>
public sealed record Standing(string PlayerId, int Score, int Place, MatchOutcome Outcome);
