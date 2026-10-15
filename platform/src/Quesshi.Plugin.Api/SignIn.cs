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

/// <summary>Completes a validated external identity using the platform's global account store.</summary>
public interface IIdentity
{
    /// <summary>
    /// Resolves an external subject before considering email. A verified email may link an
    /// existing account; an unverified email is never stored or used to select one.
    /// </summary>
    Task<ExternalIdentityCompletion> CompleteExternalAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of resolving a validated external identity.</summary>
public sealed record ExternalIdentityCompletion(ExternalIdentityCompletionState State, Guid? AccountId = null);

public enum ExternalIdentityCompletionState
{
    Completed,
    Unavailable
}
