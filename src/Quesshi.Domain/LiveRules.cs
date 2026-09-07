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

    /// <summary>How far back an abandonment still counts against a player. Tuned after watching real behaviour.</summary>
    public static readonly TimeSpan AbandonmentWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a player's rematch readiness survives before a later press by the other side no
    /// longer completes the handshake. Without this, a press answered long after the fact would drag
    /// an absent player into a live duel they would then lose to the miss-streak abandonment penalty.
    /// </summary>
    public static readonly TimeSpan RematchExpires = TimeSpan.FromMinutes(2);

    /// <summary>What the second abandonment in the window costs; the first is always free.</summary>
    public const int AbandonmentPenaltyBase = 200;

    /// <summary>However many times a player has quit in the window, the penalty climbs no higher than this.</summary>
    public const int AbandonmentPenaltyCap = 1000;

    /// <summary>
    /// The cost of the <paramref name="occurrenceInWindow"/>-th abandonment within <see cref="AbandonmentWindow"/>,
    /// counting this one: free the first time, <see cref="AbandonmentPenaltyBase"/> the second, doubling every
    /// time after that and capped at <see cref="AbandonmentPenaltyCap"/>.
    /// </summary>
    public static int AbandonmentPenalty(int occurrenceInWindow) => occurrenceInWindow switch
    {
        <= 1 => 0,
        _ => (int)Math.Min(AbandonmentPenaltyCap, (long)AbandonmentPenaltyBase << (occurrenceInWindow - 2))
    };
}
