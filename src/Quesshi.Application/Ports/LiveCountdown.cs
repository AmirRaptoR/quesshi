namespace Quesshi.Application.Ports;

/// <summary>The 3-2-1 once the second player has arrived.</summary>
public sealed record LiveCountdown(DateTimeOffset EndsAt, string ChallengerId, string OpponentId, int TotalRounds);
