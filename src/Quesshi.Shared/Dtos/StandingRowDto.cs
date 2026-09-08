namespace Quesshi.Shared;

/// <summary>
/// One participant's row on a results screen's standings list — the shape both duel kinds render
/// once a match is over, replacing the old two-score view that only ever had room for "you" and
/// "them". <see cref="Place"/> is 1-based; ties share a place exactly as <c>Standing</c>/<c>Match</c>'s
/// own competition ranking does, so two players sharing first and one below them read as 1, 1, 3.
/// <see cref="Outcome"/> is lowercase ("win"/"draw"/"loss"), the same convention as every other
/// outcome string on this API. <see cref="Expired"/> is the async-only case: a run that never
/// finished before the match itself ended (forfeiture) is not "still playing" — nothing can revive
/// it — so it is marked expired instead, rather than shown as if it might yet complete. A live duel
/// never sets this: every live participant either finishes with the match or is already ranked last
/// as an abandoner, so there is no third state to mark.
/// </summary>
public sealed record StandingRowDto(string PlayerId, string Name, string Avatar, int Score, int Place, string Outcome, bool Expired = false);
