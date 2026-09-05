namespace Quesshi.Domain;

/// <summary>
/// The numbers that are unique to a live duel. Question time, network grace, the scoring constants
/// and the difficulty ramp are shared with an async match and live on <see cref="MatchRules"/>.
/// </summary>
public static class LiveRules
{
    /// <summary>The beat between rounds, while the answer is shown.</summary>
    public static readonly TimeSpan RevealTime = TimeSpan.FromSeconds(3);

    /// <summary>3-2-1 once the second player has arrived.</summary>
    public static readonly TimeSpan StartCountdown = TimeSpan.FromSeconds(3);

    /// <summary>Consecutive silent rounds by one player that loses them the duel.</summary>
    public const int MissesBeforeAbandon = 3;

    /// <summary>A live lobby nobody joins, versus <see cref="MatchRules.ForfeitAfter"/>'s 48h for an async challenge.</summary>
    public static readonly TimeSpan LobbyExpires = TimeSpan.FromMinutes(10);

    /// <summary>A gap this long with nobody answering means the process was away, not that both players went quiet.</summary>
    public static readonly TimeSpan StaleAfter = MatchRules.QuestionTime * 2;
}
