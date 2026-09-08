namespace Quesshi.Infrastructure.Otp;

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@quesshi.app";

    /// <summary>
    /// Off only for a local catcher such as Mailpit, which speaks plain SMTP on the loopback of a
    /// machine you already trust. Any host that leaves the machine wants this on.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    /// Write the sign-in code to the log instead of sending it. A machine has to ask for this: a
    /// missing <see cref="Host"/> on its own is far more likely to be a server that lost its
    /// configuration than a developer who wants no mail, and the two must not look alike.
    /// </summary>
    public bool LogInsteadOfSending { get; set; }

    /// <summary>
    /// Which of the three a given machine is in. Development is enough on its own, because a code in
    /// a developer's console is the console they are already reading; anywhere else has to say so.
    /// </summary>
    public MailDelivery Delivery(bool isDevelopment) =>
        !string.IsNullOrWhiteSpace(Host) ? MailDelivery.Smtp
        : LogInsteadOfSending || isDevelopment ? MailDelivery.Log
        : MailDelivery.Unconfigured;
}
