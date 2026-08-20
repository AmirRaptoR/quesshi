namespace Quesshi.Shared;

public sealed record MatchDetailDto(MatchSummaryDto Summary, List<RevealedQuestionDto> Reveal);
