using Quesshi.Application.Sessions;
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
            PostgresStreamNotificationListener.Parse($"{tenantId}:{sessionId}:7"));
        var config = Assert.IsType<StreamNotification.Config>(
            PostgresStreamNotificationListener.Parse($"config:{tenantId}:8"));

        Assert.Equal((tenantId, sessionId, 7), (session.TenantId, session.SessionId, session.Version));
        Assert.Equal((tenantId, 8), (config.TenantId, config.Version));
        Assert.Null(PostgresStreamNotificationListener.Parse("not-a-stream-event"));
    }

    private sealed class Notifications : IStreamNotificationSource
    {
        public event Action<StreamNotification>? Received
        {
            add { }
            remove { }
        }
    }

    private sealed class FailingTenantDb : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default, bool readOnly = false) =>
            Task.FromException<T>(new InvalidOperationException("transient database failure"));
    }

    private sealed class MissingSnapshotLoader : ISessionSnapshotLoader
    {
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionState?>(null);

        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId,
            IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }
}
