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
    public async Task A_second_stale_admin_antiforgery_refusal_is_not_retried()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"old"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_antiforgery"}"""),
            Response(HttpStatusCode.OK, """{"token":"fresh"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_antiforgery"}"""));

        var result = await Api(handler).SetMembershipActiveAsync(Guid.Empty, true);

        Assert.Equal("admin.error", result.Error);
        Assert.Equal(["", "old", "", "fresh"], handler.Tokens);
    }

    [Fact]
    public async Task Signing_in_refreshes_the_admin_antiforgery_for_the_next_mutation()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"before-login"}"""),
            Response(HttpStatusCode.OK, """{"mustChangePassword":false}"""),
            Response(HttpStatusCode.OK, """{"token":"after-login"}"""),
            Response(HttpStatusCode.NoContent));
        var api = Api(handler);

        await api.LoginAsync("admin", "A strong password 2026!");
        await api.SetMembershipActiveAsync(Guid.Empty, true);

        Assert.Equal(["", "before-login", "", "after-login"], handler.Tokens);
    }

    [Fact]
    public async Task Signing_out_refreshes_the_admin_antiforgery_for_the_next_mutation()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"before-sign-out"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, """{"token":"after-sign-out"}"""),
            Response(HttpStatusCode.NoContent));
        var api = Api(handler);

        await api.SignOutAsync();
        await api.SetMembershipActiveAsync(Guid.Empty, true);

        Assert.Equal(["", "before-sign-out", "", "after-sign-out"], handler.Tokens);
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

    [Fact]
    public async Task A_registered_screen_mutation_uses_the_admin_antiforgery_token()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.NoContent));

        var result = await State(handler).MutateAsync<object>(HttpMethod.Post, "/api/admin/content", new { title = "Question" });

        Assert.Equal((true, "csrf"), (result.Success, handler.Tokens[1]));
    }

    [Fact]
    public async Task A_registered_screen_mutation_preserves_an_authorization_failure()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Forbidden, """{"code":"content.forbidden"}"""));

        var result = await State(handler).MutateAsync<object>(HttpMethod.Post, "/api/admin/content", new { title = "Question" });

        Assert.Equal(HttpStatusCode.Forbidden, result.Failure!.StatusCode);
    }

    [Fact]
    public async Task A_registered_screen_mutation_preserves_a_validation_failure()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest, """{"code":"content.questions.invalid"}"""));

        var result = await State(handler).MutateAsync<object>(HttpMethod.Post, "/api/admin/content", new { title = "Question" });

        Assert.Equal("content.questions.invalid", result.Failure!.Code);
    }

    [Fact]
    public async Task A_registered_screen_mutation_rejects_an_absolute_url_before_sending_a_request()
    {
        var handler = new Handler();

        await Assert.ThrowsAsync<ArgumentException>(() => State(handler).MutateAsync<object>(HttpMethod.Post, "https://other.example/api/admin/content", null));
    }

    [Fact]
    public async Task A_registered_screen_mutation_rejects_a_protocol_relative_url_before_sending_a_request()
    {
        var handler = new Handler();

        await Assert.ThrowsAsync<ArgumentException>(() => State(handler).MutateAsync<object>(HttpMethod.Post, "//other.example/api/admin/content", null));
    }

    [Fact]
    public async Task A_registered_screen_mutation_rejects_a_path_with_parent_segments_before_sending_a_request()
    {
        var handler = new Handler();

        await Assert.ThrowsAsync<ArgumentException>(() => State(handler).MutateAsync<object>(HttpMethod.Post, "/api/admin/content/../accounts", null));
    }

    private static AdminApi Api(Handler handler) => new(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
    private static AdminState State(Handler handler) => new(Api(handler));
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
