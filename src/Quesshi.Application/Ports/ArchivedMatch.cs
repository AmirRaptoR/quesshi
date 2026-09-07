using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <remarks>
/// <paramref name="IsLive"/> is the one flag that tells the async and live worlds apart in the
/// single <c>Matches</c> collection they share; it defaults to false so every row written before
/// live duels existed still reads as async.
/// </remarks>
public sealed record ArchivedMatch(
    string Id, string Code, Language Lang, string ChallengerId, string? OpponentId, string? WinnerId, bool IsDraw,
    int ChallengerScore, int OpponentScore, MatchState State, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    List<string> QuestionIds, bool IsLive = false);
