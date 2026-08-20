namespace Quesshi.Server.Auth;

public sealed class AdminAuthOptions
{
    public string Issuer { get; set; } = "quesshi";

    /// <summary>Separate from the player signing key on purpose. Set it in production.</summary>
    public string Key { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    /// <summary>An admin session is worth more than a player one, so it lasts hours, not weeks.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>Bootstrap account, created only when no administrator exists yet.</summary>
    public string BootstrapUsername { get; set; } = "admin";
    /// <summary>A placeholder: change it, or password reset has nowhere to send a link.</summary>
    public string BootstrapEmail { get; set; } = "admin@quesshi.local";

    /// <summary>Leave empty and a random one is generated and printed to the log on first start.</summary>
    public string? BootstrapPassword { get; set; }
}
