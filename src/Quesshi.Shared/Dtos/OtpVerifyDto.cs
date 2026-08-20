namespace Quesshi.Shared;

public sealed record OtpVerifyDto(string Email, string Code, string Lang = "fa");
