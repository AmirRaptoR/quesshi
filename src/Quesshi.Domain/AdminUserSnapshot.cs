namespace Quesshi.Domain;

public sealed record AdminUserSnapshot(
    string Id, string Username, string Email, string PasswordHash, bool IsActive, bool MustChangePassword,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, int FailedAttempts, DateTimeOffset? LockedUntil);
