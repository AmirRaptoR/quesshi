using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Streaming;

/// <summary>A durable-state change noticed by a silo's dedicated PostgreSQL listener.</summary>
public abstract record StreamNotification(Guid TenantId)
{
    public sealed record Session(Guid TenantId, Guid SessionId, long Version) : StreamNotification(TenantId);
    public sealed record Config(Guid TenantId, long Version) : StreamNotification(TenantId);
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
                await using (var command = new NpgsqlCommand("listen quesshi_session; listen quesshi_config;", connection))
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
                return;
            }
            catch (Exception exception)
            {
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
        if (values.Length == 3 && Guid.TryParse(values[0], out var tenant) && Guid.TryParse(values[1], out var session)
            && long.TryParse(values[2], System.Globalization.CultureInfo.InvariantCulture, out var version))
        {
            return new StreamNotification.Session(tenant, session, version);
        }
        if (values.Length == 3 && values[0] == "config" && Guid.TryParse(values[1], out tenant)
            && long.TryParse(values[2], System.Globalization.CultureInfo.InvariantCulture, out version))
        {
            return new StreamNotification.Config(tenant, version);
        }
        return null;
    }
}
