using System.Net;
using System.Text;
using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class SessionCommandsTests
{
    private const string Session = "6b0d5a0e-1d2c-4a52-9a43-0f3a7e1c9b11";

    [Fact]
    public async Task Command_is_sent_at_the_current_accepted_view_position()
    {
        var (commands, handler, _) = Create(Json(HttpStatusCode.OK, View(2)));

        var outcome = await commands.SendAsync("answer", new { choice = 1 });

        Assert.True(outcome.Succeeded);
        var request = handler.Commands.Single();
        Assert.Equal($"/api/sessions/{Session}/commands", request.Path);
        Assert.Equal(ClientHttp.BuildId, request.Build);
        var at = JsonDocument.Parse(request.Body).RootElement.GetProperty("at");
        Assert.Equal("question", at.GetProperty("phase").GetString());
        Assert.Equal(3, at.GetProperty("slot").GetInt32());
        Assert.Equal(4, at.GetProperty("step").GetInt32());
    }

    [Fact]
    public async Task Retryable_error_is_retried_exactly_once_before_returning()
    {
        var (commands, handler, _) = Create(
            Json(HttpStatusCode.Conflict, """{"error":"session_busy"}"""),
            Json(HttpStatusCode.Conflict, """{"error":"session_busy"}"""),
            Json(HttpStatusCode.OK, View(2)));

        var outcome = await commands.SendAsync("answer");

        Assert.Equal(2, handler.Commands.Count);
        Assert.False(outcome.Succeeded);
        Assert.Equal("session_busy", outcome.ErrorCode);
    }

    [Fact]
    public async Task Retry_that_succeeds_returns_the_view()
    {
        var (commands, handler, _) = Create(
            Json(HttpStatusCode.InternalServerError, """{"error":"write_failed"}"""),
            Json(HttpStatusCode.OK, View(2)));

        var outcome = await commands.SendAsync("answer");

        Assert.Equal(2, handler.Commands.Count);
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task Non_retryable_error_is_returned_without_another_request()
    {
        var (commands, handler, _) = Create(Json(HttpStatusCode.Conflict, """{"error":"late_answer"}"""));

        var outcome = await commands.SendAsync("answer");

        Assert.Single(handler.Commands);
        Assert.Equal("late_answer", outcome.ErrorCode);
    }

    [Fact]
    public async Task Newer_view_in_the_response_advances_the_shared_current_view()
    {
        var (commands, _, state) = Create(Json(HttpStatusCode.OK, View(2, slot: 4, step: 0)));

        var outcome = await commands.SendAsync("answer");

        Assert.True(outcome.ViewAdvanced);
        Assert.Equal(2, state.View!.Version);
        Assert.Equal(4, state.View.Slot);
    }

    private static (SessionCommands, Handler, PlatformState) Create(params HttpResponseMessage[] responses)
    {
        var handler = new Handler(responses);
        var http = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var state = new PlatformState(new AppConfig(http, null!));
        state.AcceptView(PlatformState.ParseView(View(1))!);
        return (new SessionCommands(new PlayerApi(http), state), handler, state);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string View(long version, int slot = 3, int step = 4) =>
        $$$"""{"id":"{{{Session}}}","code":"ABCD","format":"live","status":"active","phase":"question","slot":{{{slot}}},"step":{{{step}}},"version":{{{version}}},"configVersion":1,"now":"2026-01-01T00:00:00Z","deadline":null,"me":{"id":"p","seat":0,"role":"player","status":"active","score":0,"place":null,"outcome":null},"roster":{"total":0,"seats":[]},"content":{},"rules":{},"body":{}}""";

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);
        public List<Captured> Commands { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/api/antiforgery") return Json(HttpStatusCode.OK, """{"token":"t"}""");
            Commands.Add(new Captured(path, request.Headers.TryGetValues("X-Client-Build", out var b) ? b.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return queue.Dequeue();
        }
    }

    private sealed record Captured(string Path, string? Build, string Body);

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }
}
