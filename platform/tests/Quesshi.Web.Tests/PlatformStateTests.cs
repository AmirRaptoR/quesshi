using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PlatformStateTests
{
    private static readonly DateTimeOffset BrowserNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Session = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task An_older_stream_view_after_a_newer_one_leaves_the_current_view_unchanged()
    {
        var (state, fixture) = Create();
        await using var _ = fixture;
        await fixture.StreamAsync(2, [View(5), View(3)]);

        Assert.Equal(5, state.View!.Version);
    }

    [Fact]
    public async Task An_older_command_response_view_after_a_newer_stream_view_leaves_the_current_view_unchanged()
    {
        var (state, fixture) = Create();
        await using var _ = fixture;
        await fixture.StreamAsync(1, [View(5)]);

        Assert.False(state.AcceptView(Parse(View(4))));
        Assert.Equal(5, state.View!.Version);
        Assert.True(state.AcceptView(Parse(View(6))));
        Assert.Equal(6, state.View!.Version);
    }

    [Fact]
    public async Task An_older_inbox_version_after_a_newer_one_leaves_the_displayed_inbox_unchanged()
    {
        var (state, fixture) = Create();
        await using var _ = fixture;
        await fixture.StreamAsync(2, ["""{"version":7,"turns":[1]}""", """{"version":6,"turns":[]}"""], "inbox");

        Assert.Equal(7, state.Inbox!.Version);
        Assert.Equal(1, state.Inbox.Data.GetProperty("turns").GetArrayLength());
    }

    [Fact]
    public async Task An_older_effects_revision_after_a_newer_one_leaves_the_current_effects_unchanged()
    {
        var (state, fixture) = Create();
        await using var _ = fixture;
        await fixture.StreamAsync(2, ["""{"revision":4,"features":[{"featureId":"a","status":"done"}]}""", """{"revision":3,"features":[]}"""], "effects");

        Assert.Equal(4, state.Effects!.Revision);
        Assert.Equal(1, state.Effects.Data.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task An_older_config_version_after_a_newer_one_does_not_replace_the_tenant_configuration()
    {
        var (state, fixture) = Create();
        await using var _ = fixture;
        await fixture.Config.LoadAsync();
        await state.AcceptConfigAsync(5);
        await state.AcceptConfigAsync(4);

        Assert.Equal("Contoso", fixture.Config.Current.Name);
        Assert.Equal(5, fixture.Config.Current.ConfigVersion);
        Assert.Equal(2, fixture.ConfigRequests);
    }

    [Fact]
    public async Task An_older_config_response_does_not_replace_a_newer_loaded_configuration()
    {
        var (_, fixture) = Create(ConfigJson("Northwind", 5), ConfigJson("Stale", 4));
        await using var _ = fixture;
        await fixture.Config.LoadAsync();
        await fixture.Config.LoadAsync();

        Assert.Equal("Northwind", fixture.Config.Current.Name);
    }

    [Fact]
    public async Task Countdown_reaches_zero_at_the_servers_deadline_when_the_server_clock_is_ahead()
    {
        var (state, fixture) = Create();
        await using var _ = fixture;
        var serverNow = BrowserNow + TimeSpan.FromSeconds(5);
        state.AcceptView(Parse(View(1, serverNow, serverNow + TimeSpan.FromSeconds(10))));

        Assert.Equal(10, state.RemainingSeconds());
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, state.RemainingSeconds());
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, state.RemainingSeconds());
    }

    private static (PlatformState, Fixture) Create(params string[] configs)
    {
        var fixture = new Fixture(configs.Length > 0 ? configs : [ConfigJson("Northwind", 3), ConfigJson("Contoso", 5)]);
        return (fixture.State, fixture);
    }

    private static SessionView Parse(string json) => PlatformState.ParseView(json)!;

    private static string View(long version, DateTimeOffset? now = null, DateTimeOffset? deadline = null) =>
        $$$"""{"id":"{{{Session}}}","code":"ABCD","format":"live","status":"active","phase":"question","slot":0,"step":0,"version":{{{version}}},"configVersion":1,"now":"{{{(now ?? BrowserNow):O}}}","deadline":{{{(deadline is { } d ? $"\"{d:O}\"" : "null")}}},"me":{"id":"p","seat":0,"role":"player","status":"active","score":0,"place":null,"outcome":null},"roster":{"total":0,"seats":[]},"content":{},"rules":{},"body":{}}""";

    private static string ConfigJson(string name, long version) =>
        $$"""{"name":"{{name}}","languages":[{"code":"en","displayName":"English","direction":"ltr"}],"defaultLanguage":"en","capabilities":{},"configVersion":{{version}},"serverBuildId":"test"}""";

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = BrowserNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ConfigHandler handler;
        private readonly SseHandler sse = new();
        private readonly ClientHttp http;

        public Fixture(string[] configs)
        {
            handler = new ConfigHandler(configs);
            http = new ClientHttp(new HttpClient(new Router(handler, sse)) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
            Config = new AppConfig(http, new NullJs());
            Stream = new StreamClient(http);
            State = new PlatformState(Config, Stream, Time);
        }

        public ManualTime Time { get; } = new();
        public AppConfig Config { get; }
        public StreamClient Stream { get; }
        public PlatformState State { get; }
        public int ConfigRequests => handler.Requests;

        /// <summary>Serves the frames over a real SSE reader and returns once the state has seen all of them.</summary>
        public async Task StreamAsync(int count, string[] data, string kind = "view")
        {
            var received = 0;
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Stream.Received += _ => { if (Interlocked.Increment(ref received) >= count) complete.TrySetResult(); };
            sse.Body = string.Concat(data.Select(item => $"event: {kind}\ndata: {item}\n\n")) + "event: closed\ndata: {}\n\n";
            await Stream.SyncAsync(true, null);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            State.Dispose();
            await Stream.DisposeAsync();
        }
    }

    private sealed class ConfigHandler(string[] bodies)
    {
        private int requests;
        public int Requests => requests;
        public HttpResponseMessage Next() => new(HttpStatusCode.OK)
        {
            Content = new StringContent(bodies[Math.Min(Interlocked.Increment(ref requests) - 1, bodies.Length - 1)], Encoding.UTF8, "application/json")
        };
    }

    private sealed class SseHandler
    {
        public string Body { get; set; } = "";
        public HttpResponseMessage Open()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }
    }

    private sealed class Router(ConfigHandler config, SseHandler sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/api/stream" ? sse.Open() : config.Next());
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NullJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
