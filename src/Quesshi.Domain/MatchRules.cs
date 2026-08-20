namespace Quesshi.Domain;

/// <summary>The numbers that define what a Quesshi duel is. Everything else derives from these.</summary>
public static class MatchRules
{
    /// <summary>The length a duel takes when nobody chose one; the first entry of the list below.</summary>
    public const int QuestionsPerMatch = 10;
    public const int CategoriesPerMatch = 3;
    public const int ChoicesPerQuestion = 4;

    /// <summary>
    /// What a challenger may pick. A free number would let someone start a 200-question duel and
    /// drain a bucket; a short list keeps every length one we have content for.
    /// </summary>
    public static readonly int[] QuestionCountChoices = [10, 20, 30, 40, 50, 100];

    public static bool IsValidCount(int count) => Array.IndexOf(QuestionCountChoices, count) >= 0;

    public static readonly TimeSpan QuestionTime = TimeSpan.FromSeconds(20);

    /// <summary>Slack for the round trip, so a good answer is not punished for a slow network.</summary>
    public static readonly TimeSpan NetworkGrace = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan ForfeitAfter = TimeSpan.FromHours(48);

    public const int BaseScore = 100;
    public const int MaxSpeedBonus = 60;

    /// <summary>The difficulty of each question in a match of the default length.</summary>
    public static Difficulty LevelForSlot(int slot) => LevelForSlot(slot, QuestionsPerMatch);

    /// <summary>
    /// The ramp stretched over a match of any length: the five levels are spread evenly from the
    /// first slot to the last, so a hundred-question duel climbs in twenty-question steps and a
    /// ten-question one takes them two at a time.
    /// </summary>
    public static Difficulty LevelForSlot(int slot, int total)
    {
        if (total <= 1) return Difficulty.Medium;

        var level = (int)Math.Round(1 + 4.0 * slot / (total - 1), MidpointRounding.AwayFromZero);
        return (Difficulty)Math.Clamp(level, 1, 5);
    }

    /// <summary>Every level, low to high. Anything iterating difficulty should use this.</summary>
    public static readonly Difficulty[] AllLevels =
        [Difficulty.VeryEasy, Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.VeryHard];
}
