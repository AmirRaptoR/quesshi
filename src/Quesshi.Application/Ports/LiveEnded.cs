using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public sealed record LiveEnded(MatchState State, string? WinnerId, bool IsDraw, string? AbandonedBy, List<LivePlayerScore> Scores, string? Reason);
