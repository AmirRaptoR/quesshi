namespace Quesshi.Shared;

/// <summary>Always reports sent, whether or not the account exists. DevLink is filled in only in development.</summary>
public sealed record AdminForgotSentDto(bool Sent, string? DevLink);
