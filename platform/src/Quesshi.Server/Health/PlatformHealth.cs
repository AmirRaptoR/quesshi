using Dapper;
using Orleans.Runtime;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Health;

public sealed record PlatformHealthReport(bool Database, bool Clock, bool Silo, IReadOnlyList<BackgroundLoopStatus> Loops)
{
    public bool IsHealthy => Database && Clock && Silo && Loops.All(loop => loop.IsHealthy);
}

public sealed class PlatformHealth(DatabaseSources sources, IClockHealth clock, ISiloStatusOracle silo, IBackgroundLoopRegistry loops)
{
    public async Task<PlatformHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var database = await CanReachDatabaseAsync(cancellationToken);
        return new PlatformHealthReport(database, clock.IsHealthy, silo.CurrentStatus == SiloStatus.Active, loops.Snapshot());
    }

    private async Task<bool> CanReachDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await sources.App.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: cancellationToken));
            return true;
        }
        catch { return false; }
    }
}
