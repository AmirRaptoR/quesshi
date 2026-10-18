using System.Net;
using System.Text;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PlayerClientTests
{
    [Fact]
    public async Task Cookie_identity_loads_the_current_tenant_player()
    {
        var handler = new PlayerHandler(Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"blue","lang":"en"}"""));
        var state = State(handler);

        await state.InitializeAsync();

        Assert.Equal("Ada", state.Player?.DisplayName);
    }

    [Fact]
    public async Task Unauthorized_identity_shows_the_signed_out_entry()
    {
        var state = State(new PlayerHandler(Response(HttpStatusCode.Unauthorized)));

        await state.InitializeAsync();

        Assert.Equal(PlayerIdentityStatus.SignedOut, state.Status);
    }

    [Fact]
    public async Task Transient_identity_failure_keeps_the_entry_unconfirmed()
    {
        var state = State(new PlayerHandler(new HttpRequestException("offline")));

        await state.InitializeAsync();

        Assert.Equal(PlayerIdentityStatus.Unconfirmed, state.Status);
    }

    [Fact]
    public async Task Stale_antiforgery_token_refreshes_and_retries_one_mutation_once()
    {
        var handler = new PlayerHandler(
            Response(HttpStatusCode.OK, """{"token":"old"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_antiforgery"}"""),
            Response(HttpStatusCode.OK, """{"token":"fresh"}"""),
            Response(HttpStatusCode.Accepted, """{"status":"accepted"}"""));
        var api = Api(handler);

        var result = await api.RequestCodeAsync("ada@example.test");

        Assert.True(result.IsSuccess);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("old", handler.Requests[1].Csrf);
        Assert.Equal("fresh", handler.Requests[3].Csrf);
        Assert.All(handler.Requests, request => Assert.Equal(ClientHttp.BuildId, request.Build));
    }

    [Fact]
    public async Task Validation_failure_does_not_refresh_or_retry_a_mutation()
    {
        var handler = new PlayerHandler(
            Response(HttpStatusCode.OK, """{"token":"current"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_profile"}"""));
        var api = Api(handler);

        var result = await api.SaveProfileAsync("Ada", "avatar-1", "en");

        Assert.Equal("invalid_profile", result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Sign_in_refreshes_the_antiforgery_token_before_reloading_identity()
    {
        var handler = new PlayerHandler(
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.OK, """{"status":"signed_in"}"""),
            Response(HttpStatusCode.OK, """{"token":"after"}"""),
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"blue","lang":"en"}"""));
        var state = State(handler);

        var result = await state.VerifyCodeAsync("ada@example.test", "123456");

        Assert.True(result.IsSuccess);
        Assert.Equal("Ada", state.Player?.DisplayName);
        Assert.Equal("/api/antiforgery", handler.Requests[2].Path);
    }

    [Fact]
    public async Task Sign_out_refreshes_the_antiforgery_token_and_clears_identity()
    {
        var handler = new PlayerHandler(
            Response(HttpStatusCode.OK, """{"token":"before"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, """{"token":"after"}"""));
        var state = State(handler);

        await state.SignOutAsync();

        Assert.Equal(PlayerIdentityStatus.SignedOut, state.Status);
        Assert.Equal("/api/antiforgery", handler.Requests[2].Path);
    }

    [Fact]
    public async Task Guest_upgrade_refreshes_the_antiforgery_token_and_preserves_the_player()
    {
        var handler = new PlayerHandler(
            Response(HttpStatusCode.OK, """{"token":"before-guest"}"""),
            Response(HttpStatusCode.Created, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"blue","lang":"en"}"""),
            Response(HttpStatusCode.OK, """{"token":"guest"}"""),
            Response(HttpStatusCode.OK, """{"status":"upgraded"}"""),
            Response(HttpStatusCode.OK, """{"token":"after"}"""),
            Response(HttpStatusCode.OK, """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"blue","lang":"en"}"""));
        var state = State(handler);

        await state.CreateGuestAsync("Ada");
        var guestId = state.Player?.Id;
        var result = await state.UpgradeGuestAsync("ada@example.test", "123456");

        Assert.True(result.IsSuccess);
        Assert.Equal(guestId, state.Player?.Id);
        Assert.Equal("/api/antiforgery", handler.Requests[4].Path);
    }

    private static AuthState State(PlayerHandler handler) => new(Api(handler));

    private static PlayerApi Api(PlayerHandler handler) => new(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
    {
        Content = json is null ? new StringContent(string.Empty) : new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class PlayerHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues("X-CSRF", out var csrf) ? csrf.Single() : null,
                request.Headers.TryGetValues("X-Client-Build", out var build) ? build.Single() : null));
            return responses.Dequeue() switch
            {
                HttpResponseMessage response => Task.FromResult(response),
                Exception exception => Task.FromException<HttpResponseMessage>(exception),
                _ => throw new InvalidOperationException(),
            };
        }
    }

    private sealed record CapturedRequest(string Path, string? Csrf, string? Build);

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }
}
