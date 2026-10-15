using Microsoft.AspNetCore.Routing;

namespace Quesshi.Plugin.Api;

/// <summary>Declares an external sign-in method contributed by a plugin.</summary>
public interface ISignIn
{
    /// <summary>The stable method identifier used below <c>/api/auth/x</c>.</summary>
    string Id { get; }

    /// <summary>Typed settings, defaults, and validation for this sign-in method.</summary>
    FeatureSettings Settings { get; }

    /// <summary>Adds unauthenticated endpoints below this method's authentication route.</summary>
    void MapOpenRoutes(RouteGroupBuilder routes);
}

/// <summary>Completes a validated external identity using the platform's account and tenant player stores.</summary>
public interface IIdentity
{
    /// <summary>
    /// Resolves an external subject before considering email, then determines whether its account
    /// can sign in to <paramref name="tenant" /> or needs a tenant profile. A verified email may
    /// link an existing account; an unverified email is never stored or used to select one.
    /// </summary>
    Task<ExternalIdentityCompletion> CompleteExternalAsync(
        ExternalIdentityTenant tenant,
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken = default);
}

/// <summary>The resolved tenant context used to complete a provider-validated account sign-in.</summary>
public sealed record ExternalIdentityTenant(Guid Id, string DefaultLanguage);

/// <summary>The core outcome after linking an external identity and resolving its tenant player.</summary>
public sealed record ExternalIdentityCompletion(
    ExternalIdentityCompletionState State,
    Guid? AccountId = null,
    ExternalIdentityProfile? Profile = null);

/// <summary>The prefill and confirmation token for an account without a player in this tenant.</summary>
public sealed record ExternalIdentityProfile(string Token, string? DisplayName, string? AvatarSeed, string Language);

public enum ExternalIdentityCompletionState
{
    SignedIn,
    ProfileNeeded,
    Unavailable
}
