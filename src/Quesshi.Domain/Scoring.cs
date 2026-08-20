namespace Quesshi.Domain;

public static class Scoring
{
    /// <summary>
    /// A correct answer is worth <see cref="MatchRules.BaseScore"/> plus a speed bonus that decays
    /// linearly to zero at the buzzer. Wrong or late answers are worth nothing.
    /// </summary>
    public static int Score(bool correct, TimeSpan taken, TimeSpan limit)
    {
        if (!correct) return 0;
        if (taken > limit + MatchRules.NetworkGrace) return 0;

        var elapsed = taken < TimeSpan.Zero ? TimeSpan.Zero : (taken > limit ? limit : taken);
        var remaining = 1.0 - elapsed.TotalSeconds / limit.TotalSeconds;
        return MatchRules.BaseScore + (int)Math.Round(MatchRules.MaxSpeedBonus * remaining);
    }
}
