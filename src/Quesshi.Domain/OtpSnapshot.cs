namespace Quesshi.Domain;

public sealed record OtpSnapshot(string Email, string Code, DateTimeOffset IssuedAt, int Attempts, bool Used);
