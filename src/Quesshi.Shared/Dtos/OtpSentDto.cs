namespace Quesshi.Shared;

/// <summary>Always reports sent, whether or not the address is one anybody reads.</summary>
public sealed record OtpSentDto(bool Sent);
