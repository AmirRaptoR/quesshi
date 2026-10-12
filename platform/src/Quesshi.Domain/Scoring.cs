namespace Quesshi.Domain;

/// <summary>Calculates points for a graded answer.</summary>
public static class Scoring
{
    /// <summary>Returns the relative value of a question level, with no level treated as medium.</summary>
    public static double Weight(int? level) => level switch
    {
        1 => 0.5,
        2 => 0.8,
        4 => 1.3,
        5 => 1.6,
        _ => 1.0
    };

    /// <summary>
    /// Scores credit earned for an answer, including the level weight and speed bonus. Answers
    /// received after the limit and network grace earn no points.
    /// </summary>
    public static int Score(double credit, TimeSpan taken, TimeSpan limit, int? level = null)
    {
        if (taken > limit + MatchRules.NetworkGrace) return 0;

        var elapsed = taken < TimeSpan.Zero ? TimeSpan.Zero : (taken > limit ? limit : taken);
        var remaining = 1.0 - elapsed.TotalSeconds / limit.TotalSeconds;
        var raw = MatchRules.BaseScore + MatchRules.MaxSpeedBonus * remaining;

        return (int)Math.Round(raw * Weight(level) * credit);
    }
}
