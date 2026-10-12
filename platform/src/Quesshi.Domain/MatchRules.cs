namespace Quesshi.Domain;

/// <summary>The scoring and timing values shared by platform match formats.</summary>
public static class MatchRules
{
    /// <summary>The default time allowed to answer one question.</summary>
    public static readonly TimeSpan QuestionTime = TimeSpan.FromSeconds(20);

    /// <summary>Slack for network round trips after a question's time limit.</summary>
    public static readonly TimeSpan NetworkGrace = TimeSpan.FromSeconds(2);

    /// <summary>The points for a full-credit answer at the buzzer.</summary>
    public const int BaseScore = 100;

    /// <summary>The maximum speed bonus for a full-credit answer.</summary>
    public const int MaxSpeedBonus = 60;
}
