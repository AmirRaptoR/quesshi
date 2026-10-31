namespace Quesshi.Shared;

/// <summary>One ranked player on a leaderboard. Tied scores share a rank.</summary>
public sealed record LeaderboardRowDto(int Rank, Guid PlayerId, string Name, string Avatar, long Score);

/// <summary>
/// A board page: at most 100 entries, best first, plus the caller's own row and rank.
/// <paramref name="Me"/> is null when the caller has no score on the board (friends scope always includes the caller).
/// </summary>
public sealed record LeaderboardDto(IReadOnlyList<LeaderboardRowDto> Entries, LeaderboardRowDto? Me);
