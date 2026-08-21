using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Otp;

public sealed class SmtpAdminMailer(SmtpOptions options, ITranslator translator, ILogger<SmtpAdminMailer> logger) : IAdminMailer
{
    public async Task<string?> SendPasswordResetAsync(string email, string username, string resetLink, CancellationToken ct = default)
    {
        // Administration is an English-language surface; the game is the bilingual one.
        var subject = translator.Get(Language.En, "email.reset.subject");
        var body = string.Format(translator.Get(Language.En, "email.reset.body"),
            username, resetLink, PasswordResetToken.Lifetime.TotalMinutes);

        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseTls };
        if (!string.IsNullOrEmpty(options.User))
            client.Credentials = new NetworkCredential(options.User, options.Password);

        await client.SendMailAsync(new MailMessage(options.From, email, subject, body), ct);
        logger.LogInformation("Sent a password reset to {Email}", email);

        return null;
    }
}
