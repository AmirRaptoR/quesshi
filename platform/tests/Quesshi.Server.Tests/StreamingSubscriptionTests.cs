using Quesshi.Application.Sessions;
using Quesshi.Infrastructure.Streaming;
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
}
