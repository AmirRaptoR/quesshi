namespace Quesshi.Shared;

public sealed record StatsDto(int Wins, int Losses, int Draws, int Streak, int BestStreak, long TotalScore, int Played);
