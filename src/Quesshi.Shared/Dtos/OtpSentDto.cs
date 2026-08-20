namespace Quesshi.Shared;

/// <summary>DevCode is filled in only when the server is using the development sign-in sender.</summary>
public sealed record OtpSentDto(bool Sent, string? DevCode);
