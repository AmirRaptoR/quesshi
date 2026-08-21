using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Otp;

public sealed class SmtpAdminMailer(SmtpOptions options, ITranslator translator, ILogger<SmtpAdminMailer> logger) : IAdminMailer
{
    public async Task<string?> SendPasswordResetAsync(string email, string username, string resetLink, CancellationToken ct = default)
    {
        // Administration is an English-language surface; the game is the multilingual one.
        const Language lang = Language.En;
        var minutes = PasswordResetToken.Lifetime.TotalMinutes;

        var title = translator.Get(lang, "email.reset.title");
        var intro = string.Format(translator.Get(lang, "email.reset.intro"), username);
        var note = string.Format(translator.Get(lang, "email.reset.note"), minutes);
        var button = EmailTemplate.Button(resetLink, translator.Get(lang, "email.reset.button"));

        await SmtpMail.SendAsync(options, email, translator.Get(lang, "email.reset.subject"),
            EmailTemplate.Html(title, intro, button, note, rtl: false),
            EmailTemplate.PlainText(title, intro, resetLink, note), ct);

        logger.LogInformation("Sent a password reset to {Email}", email);
        return null;
    }
}
