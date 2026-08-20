namespace Quesshi.Shared;

public sealed record AdminAccountDto(string Id, string Username, string Email, bool IsActive, bool MustChangePassword,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, bool IsLocked);
