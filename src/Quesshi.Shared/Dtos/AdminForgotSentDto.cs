namespace Quesshi.Shared;

/// <summary>Always reports sent, whether or not the account exists.</summary>
public sealed record AdminForgotSentDto(bool Sent);
