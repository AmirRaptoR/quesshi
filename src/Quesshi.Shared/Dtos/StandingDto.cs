namespace Quesshi.Shared;

/// <summary>
/// One participant's placement once a duel is over — the wire shape of <c>Quesshi.Domain.Standing</c>.
/// <see cref="Outcome"/> is lowercased ("win"/"draw"/"loss"), the same convention every other
/// outcome string on this API already follows (see <c>MatchSummaryDto</c>'s own).
/// </summary>
public sealed record StandingDto(string PlayerId, int Score, int Place, string Outcome);
