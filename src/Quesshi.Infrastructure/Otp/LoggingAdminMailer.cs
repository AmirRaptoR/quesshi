using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Otp;

/// <summary>The admin half of <see cref="LoggingOtpSender"/>: the reset link is logged, not mailed.</summary>
public sealed class LoggingAdminMailer(ILogger<LoggingAdminMailer> logger) : IAdminMailer
{
    public Task<string?> SendPasswordResetAsync(string email, string username, string resetLink,
        CancellationToken ct = default)
    {
        logger.LogWarning("No SMTP host: the password reset for {Username} ({Email}) is {Link}",
            username, email, resetLink);
        return Task.FromResult<string?>(null);
    }
}
