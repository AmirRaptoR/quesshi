using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Otp;

/// <summary>Writes the reset link to the log and hands it back, so a local reset needs no mail server.</summary>
public sealed class ConsoleAdminMailer(ILogger<ConsoleAdminMailer> logger) : IAdminMailer
{
    public Task<string?> SendPasswordResetAsync(string email, string username, string resetLink, CancellationToken ct = default)
    {
        logger.LogWarning("Password reset for admin {Username} <{Email}>: {Link}", username, email, resetLink);
        return Task.FromResult<string?>(resetLink);
    }
}
