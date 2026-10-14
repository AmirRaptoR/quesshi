using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Identity;

namespace Quesshi.Infrastructure.Otp;

/// <summary>Delivery selection deliberately keeps SMTP ahead of development logging.</summary>
internal sealed class EmailCodeDelivery(IConfiguration configuration, ILogger<EmailCodeDelivery> logger) : IEmailCodeSender
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Smtp:Host"]) && !configuration.GetValue<bool>("Smtp:LogInsteadOfSending"))
        {
            throw new InvalidOperationException("Smtp:Host is required outside Development unless Smtp:LogInsteadOfSending is enabled.");
        }
    }

    public async Task SendAsync(string email, string code, string language, CancellationToken cancellationToken = default)
    {
        var host = configuration["Smtp:Host"];
        var content = Content(language);
        if (!string.IsNullOrWhiteSpace(host))
        {
            using var client = new SmtpClient(host, configuration.GetValue("Smtp:Port", 587))
            {
                EnableSsl = configuration.GetValue("Smtp:UseTls", true)
            };
            var user = configuration["Smtp:User"];
            if (!string.IsNullOrWhiteSpace(user))
            {
                client.Credentials = new NetworkCredential(user, configuration["Smtp:Password"]);
            }

            using var message = new MailMessage(configuration["Smtp:From"] ?? "no-reply@quesshi.app", email)
            {
                Subject = content.Subject
            };
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                EmailTemplate.PlainText(content.Title, content.Intro, code, content.Note), null, MediaTypeNames.Text.Plain));
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                EmailTemplate.Html(content.Title, content.Intro, EmailTemplate.Code(code), content.Note, content.RightToLeft), null, MediaTypeNames.Text.Html));
            await client.SendMailAsync(message, cancellationToken);
            return;
        }

        logger.LogInformation("Email sign-in code generated for {Email}: {Code}", email, code);
    }

    private static EmailContent Content(string language) => language.StartsWith("fa", StringComparison.OrdinalIgnoreCase)
        ? new("کد ورود کوئیشی", "کد ورود شما", "برای ورود این کد را وارد کن. فقط برای یک بار ورود کار می‌کند.", "این کد تا 10 دقیقه اعتبار دارد. اگر درخواست ورود نکرده‌ای، این پیام را نادیده بگیر — بدون دسترسی به ایمیلت کسی نمی‌تواند از آن استفاده کند.", true)
        : language.StartsWith("nl", StringComparison.OrdinalIgnoreCase)
            ? new("Je Quesshi-inlogcode", "Je inlogcode", "Vul deze code in om in te loggen. Hij werkt voor één keer inloggen.", "De code verloopt over 10 minuten. Heb je niet om een inlogcode gevraagd, negeer dit bericht dan — zonder toegang tot je mail kan niemand hem gebruiken.", false)
            : new("Your Quesshi sign-in code", "Your sign-in code", "Enter this code to sign in. It is good for one sign-in only.", "The code expires in 10 minutes. If you did not ask to sign in, ignore this message — nobody can use it without your inbox.", false);

    private sealed record EmailContent(string Subject, string Title, string Intro, string Note, bool RightToLeft);
}
