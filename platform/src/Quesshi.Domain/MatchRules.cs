namespace Quesshi.Domain;

/// <summary>The scoring and timing values shared by platform match formats.</summary>
public static class MatchRules
{
    public const int MinParticipants = 2;
    public const int DefaultMaxParticipants = 20;
    public const int MaxParticipants = 500;

    /// <summary>The supported sizes for a drawn async duel.</summary>
    public static readonly int[] QuestionCountChoices = [10, 20, 30, 40, 50, 100];

    public static bool IsValidCount(int count) => Array.IndexOf(QuestionCountChoices, count) >= 0;

    /// <summary>The default time allowed to answer one question.</summary>
    public static readonly TimeSpan QuestionTime = TimeSpan.FromSeconds(20);

    /// <summary>Slack for network round trips after a question's time limit.</summary>
    public static readonly TimeSpan NetworkGrace = TimeSpan.FromSeconds(2);

    /// <summary>The creation-anchored deadline for an asynchronous duel.</summary>
    public static readonly TimeSpan ForfeitAfter = TimeSpan.FromHours(48);

    /// <summary>The points for a full-credit answer at the buzzer.</summary>
    public const int BaseScore = 100;

    /// <summary>The maximum speed bonus for a full-credit answer.</summary>
    public const int MaxSpeedBonus = 60;
}
