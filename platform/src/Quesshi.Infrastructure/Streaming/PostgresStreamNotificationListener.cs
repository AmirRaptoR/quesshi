using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Streaming;

/// <summary>A durable-state change noticed by a silo's dedicated PostgreSQL listener.</summary>
public abstract record StreamNotification(Guid TenantId)
{
    public sealed record Session(Guid TenantId, Guid SessionId, long Version, string? Phase = null) : StreamNotification(TenantId);
    public sealed record Config(Guid TenantId, long Version) : StreamNotification(TenantId);
    public sealed record Player(Guid TenantId, Guid PlayerId, string Reason = "deleted") : StreamNotification(TenantId);
    public sealed record AuthenticationSession(Guid TenantId, string SessionKey) : StreamNotification(TenantId);
    public sealed record Inbox(Guid TenantId, Guid PlayerId, long Version) : StreamNotification(TenantId);
    public sealed record Effects(Guid TenantId, Guid SessionId, long Revision) : StreamNotification(TenantId);
    public sealed record Reconnected() : StreamNotification(Guid.Empty);
}

/// <summary>Exposes PostgreSQL notifications without coupling the database layer to stream delivery.</summary>
public interface IStreamNotificationSource
{
    event Action<StreamNotification>? Received;
}

/// <summary>
/// Keeps exactly one non-pooled connection per silo listening for committed stream changes.
/// A reconnect is deliberately observable so delivery can reconcile the gap before relying on
/// notifications again.
/// </summary>
public sealed class PostgresStreamNotificationListener(DatabaseSources sources, ILogger<PostgresStreamNotificationListener> logger)
    : BackgroundService, IStreamNotificationSource
{
    public event Action<StreamNotification>? Received;
    private int _backendProcessId;

    // Exposed to integration tests so they can verify the reconnect path against the dedicated
    // connection rather than a pooled sender connection.
    internal int BackendProcessId => Volatile.Read(ref _backendProcessId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(sources.SystemConnectionString) { Pooling = false };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Notification += (_, args) => Publish(Parse(args.Payload));
                await connection.OpenAsync(stoppingToken);
                Volatile.Write(ref _backendProcessId, connection.ProcessID);
                await using (var command = new NpgsqlCommand("listen quesshi_session; listen quesshi_config; listen quesshi_player; listen quesshi_inbox; listen quesshi_effects; listen quesshi_auth_session;", connection))
                {
                    await command.ExecuteNonQueryAsync(stoppingToken);
                }

                Publish(new StreamNotification.Reconnected());
                delay = TimeSpan.FromMilliseconds(100);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Volatile.Write(ref _backendProcessId, 0);
                return;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _backendProcessId, 0);
                logger.LogWarning(exception, "PostgreSQL stream listener disconnected; reconnecting before reconciliation.");
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 2_000));
            }
        }
    }

    private void Publish(StreamNotification? notification)
    {
        if (notification is not null) Received?.Invoke(notification);
    }

    internal static StreamNotification? Parse(string payload)
    {
        var values = payload.Split(':');
        if (values.Length is 3 or 4 && Guid.TryParse(values[0], out var tenant) && Guid.TryParse(values[1], out var session)
            && long.TryParse(values[2], System.Globalization.CultureInfo.InvariantCulture, out var version))
        {
            return new StreamNotification.Session(tenant, session, version, values.Length == 4 ? values[3] : null);
        }
        if (values.Length == 3 && values[0] == "config" && Guid.TryParse(values[1], out tenant)
            && long.TryParse(values[2], System.Globalization.CultureInfo.InvariantCulture, out version))
        {
            return new StreamNotification.Config(tenant, version);
        }
        if (values.Length is 3 or 4 && values[0] == "player" && Guid.TryParse(values[1], out tenant)
            && Guid.TryParse(values[2], out var playerId))
        {
            return new StreamNotification.Player(tenant, playerId, values.Length == 4 ? values[3] : "deleted");
        }
        if (values.Length == 3 && values[0] == "auth" && Guid.TryParse(values[1], out tenant)
            && values[2].Length > 0)
        {
            return new StreamNotification.AuthenticationSession(tenant, values[2]);
        }
        if (values.Length == 4 && values[0] == "inbox" && Guid.TryParse(values[1], out tenant)
            && Guid.TryParse(values[2], out playerId)
            && long.TryParse(values[3], System.Globalization.CultureInfo.InvariantCulture, out version))
        {
            return new StreamNotification.Inbox(tenant, playerId, version);
        }
        if (values.Length == 4 && values[0] == "effects" && Guid.TryParse(values[1], out tenant)
            && Guid.TryParse(values[2], out session)
            && long.TryParse(values[3], System.Globalization.CultureInfo.InvariantCulture, out version))
        {
            return new StreamNotification.Effects(tenant, session, version);
        }
        return null;
    }
}
