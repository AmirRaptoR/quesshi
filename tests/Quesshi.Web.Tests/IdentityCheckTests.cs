using System.Net;
using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The governing rule: a 401 on the startup identity call is the only outcome that discards a stored
/// token. Every other failure is inconclusive and must retain it. See issue #6.
/// </summary>
public class IdentityCheckTests
{
    [Fact]
    public void A_401_discards_the_token()
    {
        var exception = new HttpRequestException("nope", null, HttpStatusCode.Unauthorized);

        Assert.Equal(IdentityOutcome.Discard, IdentityCheck.Classify(exception));
    }

    [Fact]
    public void A_network_error_with_no_status_retains_the_token()
    {
        var exception = new HttpRequestException("connection refused");

        Assert.Equal(IdentityOutcome.Unconfirmed, IdentityCheck.Classify(exception));
    }

    [Fact]
    public void A_5xx_retains_the_token()
    {
        var exception = new HttpRequestException("boom", null, HttpStatusCode.InternalServerError);

        Assert.Equal(IdentityOutcome.Unconfirmed, IdentityCheck.Classify(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void A_non_401_4xx_retains_the_token(HttpStatusCode status)
    {
        var exception = new HttpRequestException("no", null, status);

        Assert.Equal(IdentityOutcome.Unconfirmed, IdentityCheck.Classify(exception));
    }

    [Fact]
    public void A_timeout_retains_the_token()
    {
        var exception = new TaskCanceledException("timed out", new TimeoutException());

        Assert.Equal(IdentityOutcome.Unconfirmed, IdentityCheck.Classify(exception));
    }

    [Fact]
    public void A_cancellation_retains_the_token()
    {
        var exception = new OperationCanceledException();

        Assert.Equal(IdentityOutcome.Unconfirmed, IdentityCheck.Classify(exception));
    }

    [Fact]
    public void A_malformed_body_retains_the_token()
    {
        var exception = new JsonException("unexpected token");

        Assert.Equal(IdentityOutcome.Unconfirmed, IdentityCheck.Classify(exception));
    }
}
