using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Streaming;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class StreamingNotificationDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Dedicated_listener_receives_committed_session_notifications()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        await using var sender = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await sender.OpenAsync();

        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<StreamNotification.Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Received += notification =>
        {
            if (notification is StreamNotification.Reconnected) reconnected.TrySetResult();
            if (notification is StreamNotification.Session session) received.TrySetResult(session);
        };

        await listener.StartAsync(CancellationToken.None);
        try
        {
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var tenantId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            await using var command = new NpgsqlCommand("select pg_notify('quesshi_session', @payload)", sender);
            command.Parameters.AddWithValue("payload", $"{tenantId}:{sessionId}:9");
            await command.ExecuteNonQueryAsync();

            var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal((tenantId, sessionId, 9), (notification.TenantId, notification.SessionId, notification.Version));
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    [SkippableFact]
    public async Task Two_silo_hubs_deliver_one_committed_change_within_two_hundred_milliseconds()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var firstSources = Sources();
        await using var secondSources = Sources();
        await using var sender = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await sender.OpenAsync();

        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var loader = new SnapshotLoader(State(session, 2, "player"));
        var database = new ReconciliationDatabase(session, 2);
        var first = new PostgresStreamNotificationListener(firstSources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var second = new PostgresStreamNotificationListener(secondSources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var firstHub = Hub(loader, database, first);
        var secondHub = Hub(loader, database, second);
        var firstSubscription = firstHub.Subscribe(tenant, "player", session);
        var secondSubscription = secondHub.Subscribe(tenant, "player", session);
        var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        void Observe(StreamNotification notification)
        {
            if (notification is StreamNotification.Reconnected && Interlocked.Increment(ref connections) == 2) connected.TrySetResult(0);
        }
        first.Received += Observe;
        second.Received += Observe;

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await NotifyAsync(sender, $"{tenant}:{session}:2:1");

            var views = await Task.WhenAll(ReadAsync(firstSubscription), ReadAsync(secondSubscription));
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(200), $"Delivery took {elapsed.ElapsedMilliseconds}ms.");
            Assert.All(views, view => Assert.Equal(2, view.Version));
            Assert.All(views, view => Assert.True(view.Kind == StreamMessageKind.View));
            foreach (var (subscription, view) in new[] { (firstSubscription, views[0]), (secondSubscription, views[1]) })
                Assert.True(subscription.MarkSent(view));

            await NotifyConfigAsync(sender, tenant, 3);
            var configs = await Task.WhenAll(ReadAsync(firstSubscription), ReadAsync(secondSubscription));
            Assert.All(configs, config => Assert.Equal((StreamMessageKind.Config, 3), (config.Kind, config.Version)));
        }
        finally
        {
            firstHub.Dispose();
            secondHub.Dispose();
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
            first.Dispose();
            second.Dispose();
        }
    }

    [SkippableFact]
    public async Task Reconnected_listener_reconciles_a_change_committed_during_its_connection_gap()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var sources = Sources();
        await using var admin = new NpgsqlConnection(fixture.ConnectionString!);
        await using var sender = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await admin.OpenAsync();
        await sender.OpenAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var loader = new SnapshotLoader(State(session, 1, "player"));
        var database = new ReconciliationDatabase(session, 1);
        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var hub = Hub(loader, database, listener);
        var reconnects = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        listener.Received += notification =>
        {
            if (notification is StreamNotification.Reconnected && Interlocked.Increment(ref count) == 2)
                reconnects.TrySetResult(0);
        };

        await listener.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => listener.BackendProcessId != 0);
            var subscription = hub.Subscribe(tenant, "player", session);
            await hub.RenderInitialAsync(subscription, CancellationToken.None);
            var initial = await ReadAsync(subscription);
            Assert.Equal(1, initial.Version);
            Assert.True(subscription.MarkSent(initial));

            await using var terminate = new NpgsqlCommand("select pg_terminate_backend(@pid)", admin);
            terminate.Parameters.AddWithValue("pid", listener.BackendProcessId);
            Assert.True((bool)(await terminate.ExecuteScalarAsync())!);
            loader.State = State(session, 2, "player");
            database.Version = 2;
            await NotifyAsync(sender, $"{tenant}:{session}:2:1");

            await reconnects.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, listener.BackendProcessId);
            Assert.Equal(2, (await ReadAsync(subscription)).Version);
        }
        finally
        {
            hub.Dispose();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    [SkippableFact]
    public async Task Watched_session_reconciliation_repairs_a_suppressed_notification_within_two_and_a_half_seconds()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var sources = Sources();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var loader = new SnapshotLoader(State(session, 1, "player"));
        var database = new ReconciliationDatabase(session, 1);
        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var hub = Hub(loader, database, listener);
        var connected = Reconnected(listener);

        await listener.StartAsync(CancellationToken.None);
        await connected.WaitAsync(TimeSpan.FromSeconds(10));
        var subscription = hub.Subscribe(tenant, "player", session);
        await hub.RenderInitialAsync(subscription, CancellationToken.None);
        var initial = await ReadAsync(subscription);
        Assert.True(subscription.MarkSent(initial));
        await hub.StartAsync(CancellationToken.None);
        try
        {
            loader.State = State(session, 2, "player");
            database.Version = 2;

            var repaired = await ReadAsync(subscription, TimeSpan.FromMilliseconds(2500));
            Assert.Equal((StreamMessageKind.View, 2), (repaired.Kind, repaired.Version));
        }
        finally
        {
            await hub.StopAsync(CancellationToken.None);
            hub.Dispose();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    [SkippableFact]
    public async Task App_wide_config_reconciliation_repairs_a_suppressed_notification_within_two_and_a_half_seconds()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var sources = Sources();
        var tenant = Guid.NewGuid();
        var database = new ReconciliationDatabase(Guid.NewGuid(), 0) { ConfigVersion = 1 };
        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var hub = Hub(new SnapshotLoader(null), database, listener);
        var connected = Reconnected(listener);

        await listener.StartAsync(CancellationToken.None);
        await connected.WaitAsync(TimeSpan.FromSeconds(10));
        var subscription = hub.Subscribe(tenant, "player", null);
        await hub.StartAsync(CancellationToken.None);
        try
        {
            var initial = await ReadAsync(subscription, TimeSpan.FromMilliseconds(2500));
            Assert.Equal((StreamMessageKind.Config, 1), (initial.Kind, initial.Version));
            Assert.True(subscription.MarkSent(initial));
            database.ConfigVersion = 2;

            var repaired = await ReadAsync(subscription, TimeSpan.FromMilliseconds(2500));
            Assert.Equal((StreamMessageKind.Config, 2), (repaired.Kind, repaired.Version));
        }
        finally
        {
            await hub.StopAsync(CancellationToken.None);
            hub.Dispose();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static async Task NotifyAsync(NpgsqlConnection sender, string payload)
    {
        await using var command = new NpgsqlCommand("select pg_notify('quesshi_session', @payload)", sender);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task NotifyConfigAsync(NpgsqlConnection sender, Guid tenant, long version)
    {
        await using var command = new NpgsqlCommand("select pg_notify('quesshi_config', @payload)", sender);
        command.Parameters.AddWithValue("payload", $"config:{tenant}:{version}");
        await command.ExecuteNonQueryAsync();
    }

    private static Task Reconnected(PostgresStreamNotificationListener listener)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Received += notification =>
        {
            if (notification is StreamNotification.Reconnected) connected.TrySetResult();
        };
        return connected.Task;
    }

    private static StreamHub Hub(SnapshotLoader loader, ReconciliationDatabase database, IStreamNotificationSource notifications) =>
        new(loader, new TestRenderer(), new FormatRegistry([new TestFormat()]), database, notifications,
            new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance);

    private static async Task<StreamMessage> ReadAsync(StreamSubscription subscription, TimeSpan? timeout = null) =>
        await subscription.Messages.ReadAsync().AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    private static SessionState State(Guid session, long version, string player) => SessionState.Create(
        new SessionRow(session.ToString(), null, null, new Position(1, 0), Format: "test", Version: version), Json("{}"),
        [new Seat(player, "player")], []);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Listener did not establish its connection.");
            await Task.Delay(10);
        }
    }

    private sealed class ReconciliationDatabase(Guid sessionId, long version) : ITenantDb
    {
        public long Version { get; set; } = version;
        public long? ConfigVersion { get; set; }

        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default, bool readOnly = false) =>
            callback(new ReconciliationTransaction(sessionId, Version, ConfigVersion));
    }

    private sealed class ReconciliationTransaction(Guid sessionId, long version, long? configVersion) : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>(typeof(T) == typeof(StreamHub.SessionVersion)
                ? [(T)(object)new StreamHub.SessionVersion(sessionId, version)]
                : []);

        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((T?)(object?)configVersion);
    }

    private sealed class SnapshotLoader(SessionState? state) : ISessionSnapshotLoader
    {
        public SessionState? State { get; set; } = state;

        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId,
            IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class TestRenderer : IViewRenderer
    {
        private static readonly ConstructorInfo SnapshotConstructor = typeof(SessionViewSnapshot).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(SessionState), typeof(DateTimeOffset)], null)!;
        private static readonly PropertyInfo SnapshotState = typeof(SessionViewSnapshot).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) =>
            new(state.Session.Version, "{}");

        public SessionViewSnapshot Snapshot(SessionState state, DateTimeOffset now) =>
            (SessionViewSnapshot)SnapshotConstructor.Invoke([state, now]);

        public RenderedSessionView? Render(SessionViewSnapshot snapshot, IFormat format, string viewer)
        {
            var state = (SessionState)SnapshotState.GetValue(snapshot)!;
            return state.Seats.ContainsKey(viewer) ? new RenderedSessionView(state.Session.Version, "{}") : null;
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
