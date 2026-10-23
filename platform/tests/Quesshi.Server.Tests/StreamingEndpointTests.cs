using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Tenancy;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Streaming;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class StreamingEndpointTests
{
#if QUESSHI_TEST_IDENTITY
    [Fact]
    public async Task Authenticated_app_wide_http_stream_opens_without_a_watched_session_and_reads_config()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var notifications = new Notifications();
        var hub = new StreamHub(null!, null!, null!, new EmptyTenantDb(), notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITenants>(new SingleTenant(tenant));
        builder.Services.AddSingleton(hub);
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapStream();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
            request.Headers.Host = "tenant.example.test";
            request.Headers.Add("X-Quesshi-Test-Player", Guid.NewGuid().ToString());
            var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await WaitForAsync(() => hub.SubscriptionCount == 1);
            notifications.Publish(new StreamNotification.Config(tenant.Id, 1));
            using var response = await responseTask;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
            Assert.Equal("identity", response.Content.Headers.ContentEncoding.Single());
            Assert.Equal(1, hub.SubscriptionCount);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true);
            Assert.Contains("event: config", await ReadFrameAsync(reader));
        }
        finally
        {
            await app.StopAsync();
            hub.Dispose();
        }

    }

    [Fact]
    public async Task Ordinary_http_disconnect_releases_its_subscription_before_host_shutdown()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var notifications = new Notifications();
        var hub = new StreamHub(null!, null!, null!, new EmptyTenantDb(), notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITenants>(new SingleTenant(tenant));
        builder.Services.AddSingleton(hub);
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapStream();
        await app.StartAsync();
        try
        {
            using var client = new TcpClient();
            client.Client.LingerState = new LingerOption(true, 0);
            await client.ConnectAsync(IPAddress.Loopback, new Uri(app.Urls.Single()).Port);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /api/stream HTTP/1.1\r\nHost: tenant.example.test\r\nX-Quesshi-Test-Player: {Guid.NewGuid()}\r\n\r\n"));
            await WaitForAsync(() => hub.SubscriptionCount == 1);
            notifications.Publish(new StreamNotification.Config(tenant.Id, 1));
            var response = new byte[1024];
            Assert.True(await stream.ReadAsync(response) > 0);

            client.Close();
            notifications.Publish(new StreamNotification.Config(tenant.Id, 2));
            await WaitForAsync(() => hub.SubscriptionCount == 0);
        }
        finally
        {
            await app.StopAsync();
            hub.Dispose();
        }
    }

    [Fact]
    public async Task Hosted_stream_writes_config_initial_changed_gone_and_keep_alive_frames_with_tenant_bound_access()
    {
        var first = new Tenant(Guid.NewGuid(), "first", "First", "en");
        var second = new Tenant(Guid.NewGuid(), "second", "Second", "en");
        var viewer = Guid.NewGuid();
        var session = Guid.NewGuid();
        var loader = new TenantBoundSnapshotLoader(first.Id, State(session, 1, viewer));
        var notifications = new Notifications();
        var hub = new StreamHub(loader, new NormalRenderer(), new FormatRegistry([new TestFormat()]), new EmptyTenantDb(), notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITenants>(new MultipleTenants(first, second));
        builder.Services.AddSingleton(hub);
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapStream();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var watched = await OpenAsync(client, $"/api/stream?session={session}", viewer, "first.example.test");
            Assert.Contains("event: view", await ReadFrameAsync(watched));

            loader.State = State(session, 2, viewer);
            notifications.Publish(new StreamNotification.Session(first.Id, session, 2, "1"));
            Assert.Contains("\"Version\":2", await ReadFrameAsync(watched));

            using var absent = await OpenAsync(client, $"/api/stream?session={session}", Guid.NewGuid(), "first.example.test");
            Assert.Contains("event: gone", await ReadFrameAsync(absent));
            using var otherTenant = await OpenAsync(client, $"/api/stream?session={session}", viewer, "second.example.test");
            Assert.Contains("event: gone", await ReadFrameAsync(otherTenant));

            using var appWide = await OpenAsync(client, "/api/stream", viewer, "first.example.test");
            var keepAlive = await ReadFrameAsync(appWide, TimeSpan.FromSeconds(25));
            Assert.Equal(": keep-alive\n\n", keepAlive);
        }
        finally
        {
            await app.StopAsync();
            hub.Dispose();
        }
    }
#endif

    [Fact]
    public async Task Authenticated_app_stream_disables_server_response_buffering_before_starting()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var context = new DefaultHttpContext();
        var body = new TrackingResponseBodyFeature();
        context.Features.Set<IHttpResponseBodyFeature>(body);
        context.Items[typeof(Tenant)] = tenant;
#if QUESSHI_TEST_IDENTITY
        context.Request.Headers["X-Quesshi-Test-Player"] = Guid.NewGuid().ToString();
#else
        context.Items[typeof(PlayerIdentity)] = new PlayerIdentity(tenant.Id, Guid.NewGuid(), null, false);
#endif
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hub = new StreamHub(null!, null!, null!, new EmptyTenantDb(), new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);

        await StreamEndpoints.StreamAsync(context, hub, NullLogger<StreamHub>.Instance, null, cancellation.Token);

        Assert.True(body.BufferingDisabled);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
        Assert.Equal(0, hub.SubscriptionCount);
        hub.Dispose();
    }

    [Fact]
    public async Task Watched_session_that_is_missing_sends_gone_and_releases_the_subscription()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var context = AuthenticatedContext(tenant);
        var body = new TrackingResponseBodyFeature();
        context.Features.Set<IHttpResponseBodyFeature>(body);
        var hub = new StreamHub(new MissingSnapshotLoader(), null!, null!, new EmptyTenantDb(), new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);

        await StreamEndpoints.StreamAsync(context, hub, NullLogger<StreamHub>.Instance, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("event: gone", body.Text);
        Assert.Contains("not_found", body.Text);
        Assert.Equal(0, hub.SubscriptionCount);
        hub.Dispose();
    }

    [Fact]
    public async Task Oversized_view_frame_is_replaced_with_too_large_gone_frame()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        var context = AuthenticatedContext(tenant, player);
        var body = new TrackingResponseBodyFeature();
        context.Features.Set<IHttpResponseBodyFeature>(body);
        var state = SessionState.Create(new SessionRow(session.ToString(), null, null, new Position(0, 0), Format: "test", Version: 1),
            Json("{}"), [new Seat(player.ToString(), "player")], []);
        var hub = new StreamHub(new StaticSnapshotLoader(state), new OversizedRenderer(), new FormatRegistry([new TestFormat()]), new EmptyTenantDb(),
            new Notifications(), new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);

        await StreamEndpoints.StreamAsync(context, hub, NullLogger<StreamHub>.Instance, session, CancellationToken.None);

        Assert.Contains("event: gone", body.Text);
        Assert.Contains("too_large", body.Text);
        Assert.DoesNotContain(new string('x', 256), body.Text);
        Assert.Equal(0, hub.SubscriptionCount);
        hub.Dispose();
    }

    [Fact]
    public void Real_renderer_keeps_a_five_hundred_seat_sse_view_frame_below_the_transport_cap()
    {
        var session = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var seats = Enumerable.Range(0, 500).Select(index => new Seat(index == 0 ? viewer.ToString() : Guid.NewGuid().ToString(), $"Player {index}"))
            .ToArray();
        var state = SessionState.Create(new SessionRow(session.ToString(), null, null, new Position(1, 0), Format: "test", Version: 1), Json("{}"), seats, []);
        var implementation = typeof(SessionViewSnapshot).Assembly.GetType("Quesshi.Application.Sessions.ViewRenderer", throwOnError: true)!;
        var renderer = (IViewRenderer)Activator.CreateInstance(implementation, nonPublic: true)!;

        var view = renderer.Render(state, new TestFormat(), viewer.ToString(), DateTimeOffset.UnixEpoch);

        Assert.True(Encoding.UTF8.GetByteCount($"event: view\ndata: {view.Json}\n\n") < 64 * 1024);
    }

    [Fact]
    public async Task Stalled_stream_write_times_out_and_releases_its_subscription()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var player = Guid.NewGuid();
        var session = Guid.NewGuid();
        var context = AuthenticatedContext(tenant, player);
        context.Response.Body = new StalledWriteStream();
        var state = SessionState.Create(new SessionRow(session.ToString(), null, null, new Position(0, 0), Format: "test", Version: 1),
            Json("{}"), [new Seat(player.ToString(), "player")], []);
        var hub = new StreamHub(new StaticSnapshotLoader(state), new NormalRenderer(), new FormatRegistry([new TestFormat()]), new EmptyTenantDb(),
            new Notifications(), new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);

        await StreamEndpoints.StreamAsync(context, hub, NullLogger<StreamHub>.Instance, session, CancellationToken.None);

        Assert.Equal(0, hub.SubscriptionCount);
        hub.Dispose();
    }

    [Fact]
    public async Task Unauthenticated_stream_request_is_rejected_without_a_subscription()
    {
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var context = new DefaultHttpContext();
        context.Items[typeof(Tenant)] = tenant;
        var hub = new StreamHub(null!, null!, null!, new EmptyTenantDb(), new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, TestSessionLiveness.Instance);

        await StreamEndpoints.StreamAsync(context, hub, NullLogger<StreamHub>.Instance, null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(0, hub.SubscriptionCount);
        hub.Dispose();
    }

    private static DefaultHttpContext AuthenticatedContext(Tenant tenant, Guid? player = null)
    {
        var context = new DefaultHttpContext();
        context.Items[typeof(Tenant)] = tenant;
#if QUESSHI_TEST_IDENTITY
        context.Request.Headers["X-Quesshi-Test-Player"] = (player ?? Guid.NewGuid()).ToString();
#else
        context.Items[typeof(PlayerIdentity)] = new PlayerIdentity(tenant.Id, player ?? Guid.NewGuid(), null, false);
#endif
        return context;
    }

    private sealed class TestSessionLiveness : IStreamSessionLiveness
    {
        public static TestSessionLiveness Instance { get; } = new();

        public Task<IReadOnlyDictionary<string, StreamSessionStatus>> StatusAsync(Guid tenantId, IReadOnlyCollection<string> sessionKeys,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, StreamSessionStatus>>(sessionKeys.ToDictionary(
                key => key, _ => StreamSessionStatus.Live, StringComparer.Ordinal));
    }

    private sealed class Notifications : IStreamNotificationSource
    {
        public event Action<StreamNotification>? Received;

        public void Publish(StreamNotification notification) => Received?.Invoke(notification);
    }

    private sealed class EmptyTenantDb : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default, bool readOnly = false) =>
            callback(EmptyTenantTx.Instance);
    }

    private sealed class EmptyTenantTx : ITenantTx
    {
        public static EmptyTenantTx Instance { get; } = new();

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<T>>([]);

        public Task<int> ExecuteAsync(string sql, object? parameters = null,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null,
            CancellationToken cancellationToken = default) => Task.FromResult<T?>(default);
    }

    private sealed class MissingSnapshotLoader : ISessionSnapshotLoader
    {
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionState?>(null);

        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId,
            IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class StaticSnapshotLoader(SessionState state) : ISessionSnapshotLoader
    {
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<SessionState?>(state);
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId,
            IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class TenantBoundSnapshotLoader(Guid tenantId, SessionState state) : ISessionSnapshotLoader
    {
        public SessionState State { get; set; } = state;
        public Task<SessionState?> LoadAsync(Guid requestedTenant, Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionState?>(requestedTenant == tenantId && sessionId == Guid.Parse(State.Session.Id) ? State : null);
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId,
            IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class OversizedRenderer : IViewRenderer
    {
        private static readonly ConstructorInfo SnapshotConstructor = typeof(SessionViewSnapshot).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SessionState), typeof(DateTimeOffset)], null)!;
        private static readonly PropertyInfo SnapshotState = typeof(SessionViewSnapshot).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) =>
            new(state.Session.Version, new string('x', 64 * 1024));
        public SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now) =>
            (SessionViewSnapshot)SnapshotConstructor.Invoke([state, now]);
        public RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer) =>
            new(((SessionState)SnapshotState.GetValue(snapshot)!).Session.Version, new string('x', 64 * 1024));
    }

    private sealed class NormalRenderer : IViewRenderer
    {
        private static readonly ConstructorInfo SnapshotConstructor = typeof(SessionViewSnapshot).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SessionState), typeof(DateTimeOffset)], null)!;
        private static readonly PropertyInfo SnapshotState = typeof(SessionViewSnapshot).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) =>
            state.Seats.ContainsKey(viewer) ? new(state.Session.Version, Json(state)) : null!;
        public SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now) =>
            (SessionViewSnapshot)SnapshotConstructor.Invoke([state, now]);
        public RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer)
        {
            var state = (SessionState)SnapshotState.GetValue(snapshot)!;
            return state.Seats.ContainsKey(viewer) ? new RenderedSessionView(state.Session.Version, Json(state)) : null;
        }

        private static string Json(SessionState state) => $$"""{"Version":{{state.Session.Version}}}""";
    }

    private sealed class TestFormat : IFormat
    {
        public string Id => "test";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 500, true, false, _ => true, true, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(0, []);
        public void Start(Quesshi.Engine.Contracts.ISession session) { }
        public void Apply(Quesshi.Engine.Contracts.ISession session, Command command) { }
        public void Timeout(Quesshi.Engine.Contracts.ISession session) { }
        public object View(IViewing viewing) => new { };
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static SessionState State(Guid session, long version, Guid viewer) => SessionState.Create(
        new SessionRow(session.ToString(), null, null, new Position(1, 0), Format: "test", Version: version), Json("{}"),
        [new Seat(viewer.ToString(), "viewer")], []);

#if QUESSHI_TEST_IDENTITY
    private static async Task<StreamConnection> OpenAsync(HttpClient client, string path, Guid player, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        request.Headers.Add("X-Quesshi-Test-Player", player.ToString());
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new StreamConnection(response, new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true));
    }

    private static async Task<string> ReadFrameAsync(StreamConnection connection, TimeSpan? timeout = null)
        => await ReadFrameAsync(connection.Reader, timeout);

    private static async Task<string> ReadFrameAsync(StreamReader reader, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var frame = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);
            if (line is null || line.Length == 0) return frame.Append('\n').ToString();
            frame.Append(line).Append('\n');
        }
    }

    private sealed class StreamConnection(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public StreamReader Reader { get; } = reader;
        public void Dispose()
        {
            Reader.Dispose();
            response.Dispose();
        }
    }
#endif

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private sealed class SingleTenant(Tenant tenant) : ITenants
    {
        public Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<Tenant?>(host == "tenant.example.test" ? tenant : null);
    }

    private sealed class MultipleTenants(params Tenant[] tenants) : ITenants
    {
        public Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenants.SingleOrDefault(tenant => host == $"{tenant.Slug}.example.test"));
    }

    private sealed class TrackingResponseBodyFeature : IHttpResponseBodyFeature
    {
        private readonly MemoryStream _stream = new();
        public TrackingResponseBodyFeature() => Writer = PipeWriter.Create(_stream);

        public Stream Stream => _stream;
        public PipeWriter Writer { get; }
        public bool BufferingDisabled { get; private set; }
        public string Text => Encoding.UTF8.GetString(_stream.ToArray());

        public void DisableBuffering() => BufferingDisabled = true;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class StalledWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }
}
