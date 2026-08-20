namespace Quesshi.Application.Ports;

public sealed record LeaderboardEntry(string PlayerId, long Score, int Rank);
