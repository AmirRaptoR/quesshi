namespace Quesshi.Shared;

public sealed record AuthConfigDto(bool GoogleEnabled, bool DevOtp, string? GoogleClientId);
