namespace Quesshi.Server.Auth;

public sealed class AuthOptions
{
    public string? GoogleClientId { get; set; }

    /// <summary>Local development: the sign-in screen shows the code instead of mailing it.</summary>
    public bool DevOtp { get; set; }

    /// <summary>
    /// Permits development sign-in outside Development. This hands the one-time code back in the
    /// HTTP response, so anyone can sign in as anyone: it is an account-takeover hole, not a
    /// convenience. Deliberately requires saying so out loud.
    /// </summary>
    public bool AllowInsecureDevSignIn { get; set; }
}
