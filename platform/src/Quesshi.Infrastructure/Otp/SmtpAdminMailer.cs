using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Identity;

namespace Quesshi.Infrastructure.Otp;

internal sealed class SmtpAdminMailer(IConfiguration configuration, ILogger<SmtpAdminMailer> logger) : IAdminPasswordResetMailer
{
    public async Task SendAsync(string email, string username, string resetLink, CancellationToken cancellationToken = default)
    {
        var host = configuration["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            logger.LogInformation("Admin password reset generated for {Username} at {Email}: {ResetLink}", username, email, resetLink);
            return;
        }

        using var client = new SmtpClient(host, configuration.GetValue("Smtp:Port", 587))
        {
            EnableSsl = configuration.GetValue("Smtp:UseTls", true)
        };
        var user = configuration["Smtp:User"];
        if (!string.IsNullOrWhiteSpace(user)) client.Credentials = new NetworkCredential(user, configuration["Smtp:Password"]);

        using var message = new MailMessage(configuration["Smtp:From"] ?? "no-reply@quesshi.app", email)
        {
            Subject = "Reset your Quesshi administration password",
            Body = $"Hello {username},\n\nReset your password using this one-hour link:\n{resetLink}\n\nIf you did not request this, you can ignore this email."
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}
