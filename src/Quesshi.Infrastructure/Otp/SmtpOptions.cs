namespace Quesshi.Infrastructure.Otp;

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@quesshi.app";

    /// <summary>Development only: hand the code back to the browser instead of mailing it.</summary>
    public bool Echo { get; set; }
}
