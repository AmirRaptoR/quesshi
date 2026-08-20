namespace Quesshi.Shared;

public sealed record LeaderboardRowDto(int Rank, string PlayerId, string DisplayName, string AvatarSeed, long Score);
