namespace Quesshi.Shared;

/// <summary>Reason is a translation key, so the browser decides the wording.</summary>
public sealed record AdminAuthErrorDto(string Reason, DateTimeOffset? LockedUntil = null, List<string>? Problems = null);
