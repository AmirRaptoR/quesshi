using Quesshi.Application;
using Quesshi.Features;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server.Commands;
using Npgsql;

namespace Quesshi.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["migrate"]:
                    await MigrateAsync(Environment.GetEnvironmentVariable("ConnectionStrings__Owner"));
                    return 0;
                case ["sync"]:
                    await SyncAsync();
                    return 0;
                case ["tenant", "add", .. var tenantArgs]:
                    await AddTenantAsync(tenantArgs);
                    return 0;
                default:
                    await ServeAsync(args);
                    return 0;
            }
        }
        catch (CommandException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }
    }

    public static async Task MigrateAsync(string? ownerConnectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ownerConnectionString))
        {
            throw new InvalidOperationException("ConnectionStrings__Owner is required for the migrate command.");
        }

        var migrations = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly);
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await new MigrationRunner(migrations).RunAsync(connection, cancellationToken);
    }

    internal static async Task<int> SyncAsync(DatabaseSources? sources = null, CapabilityRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        if (sources is null)
        {
            await using var environmentSources = DatabaseSources.FromEnvironment();
            return await SyncAsync(environmentSources, registry, cancellationToken);
        }

        return await new CapabilitySyncCommand(new SystemDb(sources), registry ?? CoreCapabilities.CreateRegistry())
            .ExecuteAsync(cancellationToken);
    }

    internal static async Task<Guid> AddTenantAsync(IReadOnlyList<string> args, DatabaseSources? sources = null, CapabilityRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        var options = TenantAddOptions.Parse(args);
        if (sources is null)
        {
            await using var environmentSources = DatabaseSources.FromEnvironment();
            return await AddTenantAsync(args, environmentSources, registry, cancellationToken);
        }

        return await new TenantAddCommand(new SystemDb(sources), registry ?? CoreCapabilities.CreateRegistry())
            .ExecuteAsync(options, cancellationToken);
    }

    private static Task ServeAsync(string[] args) => Task.CompletedTask;
}
