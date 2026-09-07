namespace Quesshi.Shared;

/// <summary>The answer to a round, for both players at once. <c>EndsAt</c> is when the reveal gives way to the next round.</summary>
public sealed record LiveRoundRevealDto(int Slot, int CorrectIndex, string? Explanation, List<LivePlayerRoundDto> Players, DateTimeOffset EndsAt);
