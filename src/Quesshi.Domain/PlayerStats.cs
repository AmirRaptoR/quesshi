namespace Quesshi.Domain;

public sealed record PlayerStats(int Wins, int Losses, int Draws, int Streak, int BestStreak, long TotalScore)
{
    public static readonly PlayerStats Empty = new(0, 0, 0, 0, 0, 0);

    // A custom backing field rather than the positional auto-property: the floor has to hold for a
    // `with` expression too, not only for `new PlayerStats(...)`, and only an explicit init accessor
    // runs in both places. This is what lets every caller — RecordResult, RecordAbandonment, any
    // future one — move TotalScore by plain arithmetic with no clamp of its own.
    private readonly long _totalScore = Math.Max(0, TotalScore);
    public long TotalScore
    {
        get => _totalScore;
        init => _totalScore = Math.Max(0, value);
    }

    public int Played => Wins + Losses + Draws;
}
