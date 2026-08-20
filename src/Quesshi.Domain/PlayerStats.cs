namespace Quesshi.Domain;

public sealed record PlayerStats(int Wins, int Losses, int Draws, int Streak, int BestStreak, long TotalScore)
{
    public static readonly PlayerStats Empty = new(0, 0, 0, 0, 0, 0);

    public int Played => Wins + Losses + Draws;
}
