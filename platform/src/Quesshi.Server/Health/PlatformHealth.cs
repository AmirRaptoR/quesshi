using Dapper;
using Orleans.Runtime;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Health;

public sealed record PlatformHealthReport(bool Database, bool Clock, bool Silo, IReadOnlyList<BackgroundLoopStatus> Loops)
{
    public bool IsHealthy => Database && Clock && Silo && Loops.All(loop => loop.IsHealthy);
}

public interface IPlatformDatabaseHealth
{
    Task<bool> CanReachAsync(CancellationToken cancellationToken);
}

public sealed class PlatformDatabaseHealth(DatabaseSources sources, ILogger<PlatformDatabaseHealth> logger) : IPlatformDatabaseHealth
{
    public async Task<bool> CanReachAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await sources.App.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: cancellationToken));
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Platform health database reachability check failed.");
            return false;
        }
    }
}

public interface IPlatformSiloHealth
{
    bool IsActive { get; }
}

public sealed class PlatformSiloHealth(ISiloStatusOracle silo) : IPlatformSiloHealth
{
    public bool IsActive => silo.CurrentStatus == SiloStatus.Active;
}

public sealed class PlatformHealth(IPlatformDatabaseHealth databaseHealth, IClockHealth clock, IPlatformSiloHealth silo, IBackgroundLoopRegistry loops)
{
    public async Task<PlatformHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var database = await databaseHealth.CanReachAsync(cancellationToken);
        return new PlatformHealthReport(database, clock.IsHealthy, silo.IsActive, loops.Snapshot());
    }
}
