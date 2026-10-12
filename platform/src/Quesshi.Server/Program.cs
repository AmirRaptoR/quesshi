using Quesshi.Application;
using Quesshi.Features;
using Quesshi.Infrastructure.Migrations;
using Npgsql;

namespace Quesshi.Server;

public static class Program
{
    public static Task Main(string[] args) => args is ["migrate"]
        ? MigrateAsync(Environment.GetEnvironmentVariable("ConnectionStrings__Owner"))
        : ServeAsync(args);

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

    private static Task ServeAsync(string[] args) => Task.CompletedTask;
}
