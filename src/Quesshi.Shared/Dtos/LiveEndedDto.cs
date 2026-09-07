namespace Quesshi.Shared;

public sealed record LiveEndedDto(string State, string? WinnerId, bool IsDraw, string? AbandonedBy, List<LivePlayerScoreDto> Scores, string? Reason);
