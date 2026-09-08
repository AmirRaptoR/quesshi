namespace Quesshi.Infrastructure.Otp;

/// <summary>How a machine gets a sign-in code to the person who asked for it.</summary>
public enum MailDelivery
{
    /// <summary>Sent, by the configured SMTP host.</summary>
    Smtp,

    /// <summary>Written to the log, for a machine that has said it wants no mail in the loop.</summary>
    Log,

    /// <summary>Nothing is configured and nothing has been chosen, which is a mistake rather than a mode.</summary>
    Unconfigured
}
