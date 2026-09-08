using System.Net;

namespace Quesshi.Web.Services;

/// <summary>What a startup identity check's outcome means for a token found in storage.</summary>
public enum IdentityOutcome
{
    /// <summary>The call did not tell us the token is invalid; keep it and offer a retry.</summary>
    Unconfirmed,

    /// <summary>The server rejected the token outright; there is nothing to retry.</summary>
    Discard,
}

/// <summary>
/// Decides what a failed identity call means for the token that triggered it. The one rule the app
/// follows: a 401 says the token is bad, and is the only thing that discards it. A network failure, a
/// 5xx, a non-401 4xx, a timeout, cancellation, or a malformed response body are all inconclusive —
/// none of them says the token is invalid, so all of them retain it. See issue #6.
/// </summary>
public static class IdentityCheck
{
    public static IdentityOutcome Classify(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
            ? IdentityOutcome.Discard
            : IdentityOutcome.Unconfirmed;
}
