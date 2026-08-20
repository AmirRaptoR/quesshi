namespace Quesshi.Server.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "quesshi";
    public string Audience { get; set; } = "quesshi";

    /// <summary>Set this in production. Left unset it is random per start, so restarts sign everyone out.</summary>
    public string Key { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    public int Days { get; set; } = 30;
}
