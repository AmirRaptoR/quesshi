using System.Net;
using System.Text;
using Quesshi.Web.Admin;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AdminClientTests
{
    [Fact]
    public async Task Stale_admin_antiforgery_retries_the_mutation_once()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"old"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_antiforgery"}"""),
            Response(HttpStatusCode.OK, """{"token":"fresh"}"""),
            Response(HttpStatusCode.NoContent));

        await Api(handler).SetMembershipActiveAsync(Guid.Empty, true);

        Assert.Equal(["", "old", "", "fresh"], handler.Tokens);
    }

    [Fact]
    public async Task Admin_requests_use_the_admin_antiforgery_header_and_client_build()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, """{"token":"token"}"""), Response(HttpStatusCode.NoContent));

        await Api(handler).SetMembershipActiveAsync(Guid.Empty, true);

        Assert.Equal(ClientHttp.BuildId, handler.Builds[1]);
    }

    [Fact]
    public async Task Admin_antiforgery_requests_are_uncached()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, """{"token":"token"}"""), Response(HttpStatusCode.NoContent));

        await Api(handler).SetMembershipActiveAsync(Guid.Empty, true);

        Assert.True(handler.CacheControls[0]);
    }

    [Fact]
    public async Task Admin_requests_never_send_a_player_antiforgery_token()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, """{"token":"token"}"""), Response(HttpStatusCode.NoContent));

        await Api(handler).SetMembershipActiveAsync(Guid.Empty, true);

        Assert.All(handler.PlayerTokens, Assert.False);
    }

    private static AdminApi Api(Handler handler) => new(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status) { Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<string> Tokens { get; } = [];
        public List<string?> Builds { get; } = [];
        public List<bool> CacheControls { get; } = [];
        public List<bool> PlayerTokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.TryGetValues("X-CSRF-Admin", out var token) ? token.Single() : string.Empty);
            Builds.Add(request.Headers.TryGetValues("X-Client-Build", out var build) ? build.Single() : null);
            CacheControls.Add(request.Headers.CacheControl is { NoCache: true, NoStore: true });
            PlayerTokens.Add(request.Headers.Contains("X-CSRF"));
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class NullReloader : IClientReloader { public Task ReloadOnceAsync(string buildId) => Task.CompletedTask; }
}
