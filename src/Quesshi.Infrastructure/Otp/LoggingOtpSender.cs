using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Otp;

/// <summary>
/// The sender for a machine with no SMTP host at all: nothing leaves the process and the code goes
/// to the log, which is the only other place a developer can read it. It is still never handed back
/// to the caller — an endpoint that returns the code it has just issued is a way to sign in as
/// anyone who has an address.
/// </summary>
public sealed class LoggingOtpSender(ILogger<LoggingOtpSender> logger) : IOtpSender
{
    public Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default)
    {
        logger.LogWarning("No SMTP host: the sign-in code for {Email} is {Code}", email, code);
        return Task.FromResult<string?>(null);
    }
}
