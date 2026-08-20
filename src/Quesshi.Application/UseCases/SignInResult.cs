using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

public sealed record SignInResult(OtpResult Result, Player? Player);
