using System.Net;
using System.Net.Mail;
using System.Net.Mime;

namespace Quesshi.Infrastructure.Otp;

/// <summary>The bit both senders do the same way: build the message, connect, send.</summary>
internal static class SmtpMail
{
    public static async Task SendAsync(SmtpOptions options, string to, string subject,
        string html, string text, CancellationToken ct)
    {
        using var message = new MailMessage(options.From, to) { Subject = subject };

        // Text first: a client that understands neither shows the first part, and the order is how
        // a multipart/alternative message says which version it would rather you saw.
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, null, MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, MediaTypeNames.Text.Html));

        using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseTls };
        if (!string.IsNullOrEmpty(options.User))
            client.Credentials = new NetworkCredential(options.User, options.Password);

        await client.SendMailAsync(message, ct);
    }
}
