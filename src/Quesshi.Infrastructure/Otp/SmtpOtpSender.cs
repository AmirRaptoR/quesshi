using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Otp;

public sealed class SmtpOtpSender(SmtpOptions options, ITranslator translator, ILogger<SmtpOtpSender> logger) : IOtpSender
{
    public async Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default)
    {
        var subject = translator.Get(lang, "email.otp.subject");
        var body = string.Format(translator.Get(lang, "email.otp.body"), code, OtpChallenge.Lifetime.TotalMinutes);

        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseTls };
        if (!string.IsNullOrEmpty(options.User))
            client.Credentials = new NetworkCredential(options.User, options.Password);

        await client.SendMailAsync(new MailMessage(options.From, email, subject, body), ct);
        logger.LogInformation("Sent a sign-in code to {Email}", email);

        return null;
    }
}
