using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Otp;

public sealed class SmtpOtpSender(SmtpOptions options, ITranslator translator, ILogger<SmtpOtpSender> logger) : IOtpSender
{
    public async Task<string?> SendAsync(string email, string code, Language lang, CancellationToken ct = default)
    {
        var minutes = OtpChallenge.Lifetime.TotalMinutes;

        var title = translator.Get(lang, "email.otp.title");
        var intro = translator.Get(lang, "email.otp.intro");
        var note = string.Format(translator.Get(lang, "email.otp.note"), minutes);

        await SmtpMail.SendAsync(options, email, translator.Get(lang, "email.otp.subject"),
            EmailTemplate.Html(title, intro, EmailTemplate.Code(code), note, rtl: lang == Language.Fa),
            EmailTemplate.PlainText(title, intro, code, note), ct);

        logger.LogInformation("Sent a sign-in code to {Email}", email);
        return null;
    }
}
