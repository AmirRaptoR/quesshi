using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Server;
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
}
