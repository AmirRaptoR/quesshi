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

    [SkippableFact]
    public async Task Two_silo_listeners_receive_one_committed_change_within_two_hundred_milliseconds()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var firstSources = Sources();
        await using var secondSources = Sources();
        await using var sender = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await sender.OpenAsync();

        var first = new PostgresStreamNotificationListener(firstSources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var second = new PostgresStreamNotificationListener(secondSources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<StreamNotification.Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var deliveries = 0;
        void Observe(StreamNotification notification)
        {
            if (notification is StreamNotification.Reconnected && Interlocked.Increment(ref connections) == 2) connected.TrySetResult(0);
            if (notification is StreamNotification.Session session && Interlocked.Increment(ref deliveries) == 2) received.TrySetResult(session);
        }
        first.Received += Observe;
        second.Received += Observe;

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var tenantId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await NotifyAsync(sender, $"{tenantId}:{sessionId}:9:1");

            var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(200), $"Delivery took {elapsed.ElapsedMilliseconds}ms.");
            Assert.Equal((tenantId, sessionId, 9, "1"), (notification.TenantId, notification.SessionId, notification.Version, notification.Phase));
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
            first.Dispose();
            second.Dispose();
        }
    }

    [SkippableFact]
    public async Task Listener_reconnects_after_its_dedicated_connection_is_terminated()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var sources = Sources();
        await using var admin = new NpgsqlConnection(fixture.ConnectionString!);
        await admin.OpenAsync();
        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
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
            await using var terminate = new NpgsqlCommand("select pg_terminate_backend(@pid)", admin);
            terminate.Parameters.AddWithValue("pid", listener.BackendProcessId);
            Assert.True((bool)(await terminate.ExecuteScalarAsync())!);

            await reconnects.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, listener.BackendProcessId);
        }
        finally
        {
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Listener did not establish its connection.");
            await Task.Delay(10);
        }
    }
}
