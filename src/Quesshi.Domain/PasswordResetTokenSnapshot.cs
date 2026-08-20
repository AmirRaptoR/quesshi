namespace Quesshi.Domain;

public sealed record PasswordResetTokenSnapshot(string AdminUserId, string SecretHash, DateTimeOffset IssuedAt, bool Used);
