using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Otp;

/// <summary>Writes the code to the log and returns it, so local sign-in needs no mail server.</summary>
public sealed class ConsoleOtpSender(ILogger<ConsoleOtpSender> logger) : IOtpSender
{
    public Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default)
    {
        logger.LogWarning("Sign-in code for {Email} is {Code} (development sender — never enable this in production)", email, code);
        return Task.FromResult<string?>(code);
    }
}
