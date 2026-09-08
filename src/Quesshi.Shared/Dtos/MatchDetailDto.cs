namespace Quesshi.Shared;

/// <summary>
/// <see cref="Standings"/> is issue #53's addition: every participant's row once the match is over
/// (empty while it is still running, or for a <c>NoContest</c>, which credits nobody — see
/// <see cref="StandingRowDto"/>'s own remarks). <see cref="Summary"/> keeps its own two-sided
/// <c>Me</c>/<c>Opponent</c> shape for the rosette and the "still catching up" copy that only ever
/// needed one other side to name; <see cref="Standings"/> is what the results screen's standings list
/// renders instead of that shape's <c>Opponent</c> alone.
/// </summary>
public sealed record MatchDetailDto(MatchSummaryDto Summary, List<RevealedQuestionDto> Reveal, List<StandingRowDto> Standings);
