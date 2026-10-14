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
using Quesshi.Server.Security;
using Quesshi.Server.Streaming;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class StreamingEndpointTests
{
#if QUESSHI_TEST_IDENTITY
    [Fact]
    public async Task Authenticated_app_wide_http_stream_opens_without_a_watched_session_and_releases_its_subscription()
    {
        var port = AvailablePort();
        var tenant = new Tenant(Guid.NewGuid(), "tenant", "Tenant", "en");
        var notifications = new Notifications();
        var hub = new StreamHub(null!, null!, null!, null!, notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<ITenants>(new SingleTenant(tenant));
        builder.Services.AddSingleton(hub);
        await using var app = builder.Build();
        app.UseTenantResolution();
        app.MapStream();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
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
            response.Dispose();
        }
        finally
        {
            await app.StopAsync();
            hub.Dispose();
        }

        Assert.Equal(0, hub.SubscriptionCount);
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
        var hub = new StreamHub(null!, null!, null!, null!, new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

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
        var hub = new StreamHub(new MissingSnapshotLoader(), null!, null!, null!, new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

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
        var hub = new StreamHub(new StaticSnapshotLoader(state), new OversizedRenderer(), new FormatRegistry([new TestFormat()]), null!,
            new Notifications(), new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

        await StreamEndpoints.StreamAsync(context, hub, NullLogger<StreamHub>.Instance, session, CancellationToken.None);

        Assert.Contains("event: gone", body.Text);
        Assert.Contains("too_large", body.Text);
        Assert.DoesNotContain(new string('x', 256), body.Text);
        Assert.Equal(0, hub.SubscriptionCount);
        hub.Dispose();
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
        var hub = new StreamHub(new StaticSnapshotLoader(state), new NormalRenderer(), new FormatRegistry([new TestFormat()]), null!,
            new Notifications(), new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

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
        var hub = new StreamHub(null!, null!, null!, null!, new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

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

    private sealed class Notifications : IStreamNotificationSource
    {
        public event Action<StreamNotification>? Received;

        public void Publish(StreamNotification notification) => Received?.Invoke(notification);
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

        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => new(state.Session.Version, "{}");
        public SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now) =>
            (SessionViewSnapshot)SnapshotConstructor.Invoke([state, now]);
        public RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer) =>
            new(((SessionState)SnapshotState.GetValue(snapshot)!).Session.Version, "{}");
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

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

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
