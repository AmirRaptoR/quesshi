namespace Quesshi.Web.Services;

/// <summary>Whether the viewer may use packs: a signed-in non-guest player of a tenant with the <c>custom_questions</c> switch on.</summary>
public sealed class PackAvailability(AppConfig config, AuthState auth)
{
    public const string Switch = "custom_questions";

    public bool Available =>
        auth.Status == PlayerIdentityStatus.SignedIn
        && auth.Player is { IsGuest: false }
        && config.Current?.Capabilities.TryGetValue("switch", out var switches) == true
        && switches.Contains(Switch, StringComparer.Ordinal);
}
