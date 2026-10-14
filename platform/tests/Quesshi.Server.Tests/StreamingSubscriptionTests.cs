using Quesshi.Application.Sessions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server.Streaming;

namespace Quesshi.Server.Tests;

public sealed class StreamingSubscriptionTests
{
    [Fact]
    public void Newest_view_replaces_pending_view_and_an_older_view_never_follows_it()
    {
        var subscription = new StreamSubscription(Guid.NewGuid(), "player", Guid.NewGuid());

        subscription.QueueView(new RenderedSessionView(1, "{\"version\":1}"));
        subscription.QueueView(new RenderedSessionView(2, "{\"version\":2}"));

        Assert.True(subscription.Messages.TryRead(out var current));
        Assert.Equal(StreamMessageKind.View, current.Kind);
        Assert.Equal(2, current.Version);
        Assert.True(subscription.MarkSent(current));

        subscription.QueueView(new RenderedSessionView(1, "{\"version\":1}"));
        subscription.QueueView(new RenderedSessionView(3, "{\"version\":3}"));

        Assert.True(subscription.Messages.TryRead(out var next));
        Assert.Equal(3, next.Version);
        Assert.True(subscription.MarkSent(next));
        Assert.False(subscription.MarkSent(current));
    }

    [Fact]
    public void Initial_version_zero_view_is_delivered()
    {
        var subscription = new StreamSubscription(Guid.NewGuid(), "player", Guid.NewGuid());

        subscription.QueueView(new RenderedSessionView(0, "{\"version\":0}"));

        Assert.True(subscription.Messages.TryRead(out var view));
        Assert.Equal(0, view.Version);
        Assert.True(subscription.MarkSent(view));
        Assert.False(subscription.NeedsView(0));
    }

    [Fact]
    public void Config_does_not_displace_a_pending_view()
    {
        var subscription = new StreamSubscription(Guid.NewGuid(), "player", Guid.NewGuid());
        subscription.QueueView(new RenderedSessionView(4, "{\"version\":4}"));

        subscription.QueueConfig(2);

        Assert.True(subscription.Messages.TryRead(out var message));
        Assert.Equal(StreamMessageKind.View, message.Kind);
        Assert.Equal(4, message.Version);
    }

    [Fact]
    public async Task Config_notifications_are_fanned_out_only_within_their_tenant()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var notifications = new Notifications();
        var hub = new StreamHub(null!, null!, null!, null!, notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);
        var first = hub.Subscribe(tenant, "first", null);
        var second = hub.Subscribe(tenant, "second", null);
        var other = hub.Subscribe(otherTenant, "other", null);

        notifications.Publish(new StreamNotification.Config(tenant, 7));

        Assert.Equal(7, (await ReadAsync(first)).Version);
        Assert.Equal(7, (await ReadAsync(second)).Version);
        Assert.False(other.Messages.TryRead(out _));
        hub.Dispose();
    }

    [Fact]
    public async Task Reconnect_reconciles_watched_sessions_without_another_notification()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var notifications = new Notifications();
        var loader = new RecordingSnapshotLoader(State(session, 4, 1));
        var hub = new StreamHub(loader, new RecordingRenderer(), new FormatRegistry([new TestFormat()]),
            new ReconciliationDatabase(session, 4), notifications, new TestClock(DateTimeOffset.UnixEpoch),
            NullLogger<StreamHub>.Instance);
        var subscription = hub.Subscribe(tenant, "player-0", session);

        notifications.Publish(new StreamNotification.Reconnected());

        Assert.Equal(4, (await ReadAsync(subscription)).Version);
        Assert.Equal(1, loader.LoadCount);
        hub.Dispose();
    }

    [Fact]
    public async Task Reconciliation_failure_is_logged_and_does_not_escape()
    {
        var hub = new StreamHub(null!, null!, null!, new FailingTenantDb(), new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);
        hub.Subscribe(Guid.NewGuid(), "player", null);

        await hub.ReconcileAsync();

        hub.Dispose();
    }

    [Fact]
    public async Task Unsubscribing_the_last_watcher_releases_its_idle_render_gate()
    {
        var hub = new StreamHub(new MissingSnapshotLoader(), null!, null!, null!, new Notifications(),
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);
        var subscription = hub.Subscribe(Guid.NewGuid(), "player", Guid.NewGuid());

        await hub.RenderInitialAsync(subscription, CancellationToken.None);

        Assert.Equal(1, hub.SessionGateCount);
        hub.Unsubscribe(subscription);
        Assert.Equal(0, hub.SessionGateCount);
        hub.Dispose();
    }

    [Fact]
    public void Listener_parses_session_and_config_notifications()
    {
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var session = Assert.IsType<StreamNotification.Session>(
            PostgresStreamNotificationListener.Parse($"{tenantId}:{sessionId}:7:3"));
        var config = Assert.IsType<StreamNotification.Config>(
            PostgresStreamNotificationListener.Parse($"config:{tenantId}:8"));

        Assert.Equal((tenantId, sessionId, 7, "3"), (session.TenantId, session.SessionId, session.Version, session.Phase));
        Assert.Equal((tenantId, 8), (config.TenantId, config.Version));
        Assert.Null(PostgresStreamNotificationListener.Parse("not-a-stream-event"));
    }

    [Fact]
    public async Task Phase_changes_bypass_score_coalescing_and_share_one_snapshot_across_subscribers()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var notifications = new Notifications();
        var loader = new RecordingSnapshotLoader(State(session, 1, 500));
        var renderer = new RecordingRenderer();
        var hub = Hub(loader, renderer, notifications);
        var subscribers = Enumerable.Range(0, 500).Select(index => hub.Subscribe(tenant, $"player-{index}", session)).ToArray();

        notifications.Publish(new StreamNotification.Session(tenant, session, 1, "0"));
        var first = await ReadAsync(subscribers[0]);
        Assert.Equal(1, first.Version);
        foreach (var subscriber in subscribers.Skip(1)) Assert.Equal(1, (await ReadAsync(subscriber)).Version);
        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(1, renderer.SnapshotCount);
        Assert.Equal(500, renderer.RenderCount);

        loader.State = State(session, 2, 500);
        notifications.Publish(new StreamNotification.Session(tenant, session, 2, "0"));
        await Task.Delay(75);
        Assert.False(subscribers[0].Messages.TryRead(out _));

        loader.State = State(session, 3, 500, phase: 1);
        var started = System.Diagnostics.Stopwatch.StartNew();
        notifications.Publish(new StreamNotification.Session(tenant, session, 3, "1"));
        Assert.Equal(3, (await ReadAsync(subscribers[0])).Version);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(200));
        hub.Dispose();
    }

    [Fact]
    public async Task Score_only_bursts_are_limited_to_four_renders_per_second()
    {
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var notifications = new Notifications();
        var loader = new RecordingSnapshotLoader(State(session, 0, 1));
        var renderer = new RecordingRenderer();
        var hub = Hub(loader, renderer, notifications);
        var subscription = hub.Subscribe(tenant, "player-0", session);

        notifications.Publish(new StreamNotification.Session(tenant, session, 0, "0"));
        await ReadAsync(subscription);
        renderer.ResetSnapshots();
        for (var version = 1; version <= 10; version++)
        {
            loader.State = State(session, version, 1);
            notifications.Publish(new StreamNotification.Session(tenant, session, version, "0"));
            await Task.Delay(70);
        }
        await Task.Delay(350);

        var snapshotTimes = renderer.SnapshotTimes;
        Assert.NotEmpty(snapshotTimes);
        Assert.All(snapshotTimes.Zip(snapshotTimes.Skip(1)), pair =>
            Assert.True(Stopwatch.GetElapsedTime(pair.First, pair.Second) >= TimeSpan.FromMilliseconds(240)));
        hub.Unsubscribe(subscription);
        hub.Dispose();
    }

    [Fact]
    public async Task Slow_render_drops_an_older_pending_view_and_render_jobs_never_exceed_eight()
    {
        var tenant = Guid.NewGuid();
        var notifications = new Notifications();
        var loader = new RecordingSnapshotLoader(State(Guid.NewGuid(), 1, 1)) { Delay = TimeSpan.FromMilliseconds(125) };
        var renderer = new RecordingRenderer();
        var hub = Hub(loader, renderer, notifications);
        var subscriptions = Enumerable.Range(0, 9).Select(index =>
        {
            var session = Guid.NewGuid();
            return (Session: session, Subscription: hub.Subscribe(tenant, "player-0", session));
        }).ToArray();

        foreach (var (session, _) in subscriptions)
        {
            loader.States[session] = State(session, 1, 1);
            notifications.Publish(new StreamNotification.Session(tenant, session, 1, "0"));
        }
        foreach (var (_, subscription) in subscriptions) Assert.Equal(1, (await ReadAsync(subscription)).Version);
        Assert.InRange(loader.MaximumConcurrentLoads, 1, 8);

        var watched = subscriptions[0];
        loader.Delay = TimeSpan.FromMilliseconds(125);
        loader.States[watched.Session] = State(watched.Session, 2, 1);
        loader.State = loader.States[watched.Session];
        notifications.Publish(new StreamNotification.Session(tenant, watched.Session, 2, "1"));
        await loader.WaitForLoadAsync(10);
        loader.States[watched.Session] = State(watched.Session, 3, 1, phase: 2);
        loader.State = loader.States[watched.Session];
        notifications.Publish(new StreamNotification.Session(tenant, watched.Session, 3, "2"));

        Assert.Equal(3, (await ReadAsync(watched.Subscription)).Version);
        Assert.Equal(1, loader.MaximumConcurrentLoadsFor(watched.Session));
        hub.Dispose();
    }

    private sealed class Notifications : IStreamNotificationSource
    {
        public event Action<StreamNotification>? Received;
        public void Publish(StreamNotification notification) => Received?.Invoke(notification);
    }

    private sealed class FailingTenantDb : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default, bool readOnly = false) =>
            Task.FromException<T>(new InvalidOperationException("transient database failure"));
    }

    private sealed class ReconciliationDatabase(Guid sessionId, long version) : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default, bool readOnly = false) =>
            callback(new ReconciliationTransaction(sessionId, version));
    }

    private sealed class ReconciliationTransaction(Guid sessionId, long version) : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>([(T)(object)new StreamHub.SessionVersion(sessionId, version)]);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<T?>(default);
    }


    private sealed class MissingSnapshotLoader : ISessionSnapshotLoader
    {
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionState?>(null);

        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId,
            IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private static StreamHub Hub(RecordingSnapshotLoader loader, RecordingRenderer renderer, Notifications notifications) =>
        new(loader, renderer, new FormatRegistry([new TestFormat()]), null!, notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

    private static async Task<StreamMessage> ReadAsync(StreamSubscription subscription) =>
        await subscription.Messages.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static SessionState State(Guid session, long version, int players, int phase = 0) => SessionState.Create(
        new SessionRow(session.ToString(), null, null, new Position(phase, 0), Format: "test", Version: version), Json("{}"),
        Enumerable.Range(0, players).Select(index => new Seat($"player-{index}", "player")), []);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class RecordingSnapshotLoader(SessionState state) : ISessionSnapshotLoader
    {
        private readonly Dictionary<Guid, int> _activeBySession = [];
        private readonly Dictionary<Guid, int> _maximumBySession = [];
        private int _active;
        private int _maximum;
        private int _loads;

        public Dictionary<Guid, SessionState> States { get; } = [];
        public SessionState State { get; set; } = state;
        public TimeSpan Delay { get; set; }
        public int LoadCount => Volatile.Read(ref _loads);
        public int MaximumConcurrentLoads => Volatile.Read(ref _maximum);
        public TaskCompletionSource<int> LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximum, active);
            lock (_activeBySession)
            {
                var sessionActive = _activeBySession.TryGetValue(sessionId, out var current) ? current + 1 : 1;
                _activeBySession[sessionId] = sessionActive;
                _maximumBySession[sessionId] = Math.Max(_maximumBySession.GetValueOrDefault(sessionId), sessionActive);
            }
            var count = Interlocked.Increment(ref _loads);
            LoadStarted.TrySetResult(count);
            var result = States.GetValueOrDefault(sessionId, State);
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                return result;
            }
            finally
            {
                lock (_activeBySession) _activeBySession[sessionId]--;
                Interlocked.Decrement(ref _active);
            }
        }

        public async Task WaitForLoadAsync(int count)
        {
            while (LoadCount < count) await Task.Delay(5);
        }

        public int MaximumConcurrentLoadsFor(Guid session)
        {
            lock (_activeBySession) return _maximumBySession.GetValueOrDefault(session);
        }

        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionItemState>>([]);

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (candidate > (maximum = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, candidate, maximum) != maximum) { }
        }
    }

    private sealed class RecordingRenderer : IViewRenderer
    {
        private static readonly ConstructorInfo SnapshotConstructor = typeof(SessionViewSnapshot).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SessionState), typeof(DateTimeOffset)], null)!;
        private static readonly PropertyInfo SnapshotState = typeof(SessionViewSnapshot).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private int _snapshots;
        private int _renders;
        private readonly ConcurrentQueue<long> _snapshotTimes = new();
        public int SnapshotCount => Volatile.Read(ref _snapshots);
        public int RenderCount => Volatile.Read(ref _renders);
        public long[] SnapshotTimes => _snapshotTimes.ToArray();

        public void ResetSnapshots()
        {
            Interlocked.Exchange(ref _snapshots, 0);
            while (_snapshotTimes.TryDequeue(out _)) { }
        }

        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => new(state.Session.Version, "{}");
        public SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now)
        {
            Interlocked.Increment(ref _snapshots);
            _snapshotTimes.Enqueue(Stopwatch.GetTimestamp());
            return (SessionViewSnapshot)SnapshotConstructor.Invoke([state, now]);
        }
        public RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer)
        {
            Interlocked.Increment(ref _renders);
            return new RenderedSessionView(((SessionState)SnapshotState.GetValue(snapshot)!).Session.Version, "{}");
        }
    }

    private sealed class TestFormat : IFormat
    {
        public string Id => "test";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 500, true, false, _ => true, true, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(0, []);
        public void Start(ISession session) { }
        public void Apply(ISession session, Command command) { }
        public void Timeout(ISession session) { }
        public object View(IViewing viewing) => new { };
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }
}
