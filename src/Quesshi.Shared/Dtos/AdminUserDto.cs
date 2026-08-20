namespace Quesshi.Shared;

public sealed record AdminUserDto(string Id, string DisplayName, string Email, string AvatarSeed, bool IsBanned,
    StatsDto Stats, DateTimeOffset CreatedAt);
