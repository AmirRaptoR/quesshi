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
}
