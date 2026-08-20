namespace Quesshi.Domain;

public static class Scoring
{
    /// <summary>
    /// What a level is worth against Medium. Flat scoring made an easy duel the better way to earn
    /// a rank: the questions score the same and are answered faster, so they take a larger speed
    /// bonus too. Now the hard ones pay for being hard.
    /// </summary>
    public static double Weight(Difficulty level) => level switch
    {
        Difficulty.VeryEasy => 0.5,
        Difficulty.Easy => 0.8,
        Difficulty.Hard => 1.3,
        Difficulty.VeryHard => 1.6,
        _ => 1.0
    };

    /// <summary>
    /// A correct answer is worth <see cref="MatchRules.BaseScore"/> plus a speed bonus that decays
    /// linearly to zero at the buzzer, the whole thing scaled by what the level is worth. Wrong or
    /// late answers are worth nothing.
    /// </summary>
    public static int Score(bool correct, TimeSpan taken, TimeSpan limit, Difficulty level = Difficulty.Medium)
    {
        if (!correct) return 0;
        if (taken > limit + MatchRules.NetworkGrace) return 0;

        var elapsed = taken < TimeSpan.Zero ? TimeSpan.Zero : (taken > limit ? limit : taken);
        var remaining = 1.0 - elapsed.TotalSeconds / limit.TotalSeconds;
        var raw = MatchRules.BaseScore + MatchRules.MaxSpeedBonus * remaining;

        return (int)Math.Round(raw * Weight(level));
    }
}
