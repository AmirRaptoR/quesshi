using System.Diagnostics;
using Npgsql;
using Quesshi.Application;
using Quesshi.Features;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

public sealed class MigrationRunnerIsolationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Isolated_database_migrations_hold_independent_advisory_locks()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var firstConnectionString = Assert.IsType<string>(fixture.ConnectionString);
        var second = new PostgresFixture();
        Task[]? runs = null;
        Exception? testFailure = null;
        try
        {
            await second.InitializeAsync();
            var secondConnectionString = Assert.IsType<string>(second.ConnectionString);
            await Task.WhenAll(
                Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner")),
                Program.MigrateAsync(second.ConnectionStringForRole("quesshi_owner")));

            var scripts = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly)
                .Append(new MigrationScript("20991231T2359_core_isolated_lock.sql", "core", "select pg_sleep(3);"u8.ToArray()));
            var runner = new MigrationRunner(scripts);
            runs =
            [
                RunAsync(runner, fixture.ConnectionStringForRole("quesshi_owner")),
                RunAsync(runner, second.ConnectionStringForRole("quesshi_owner"))
            ];

            await using var firstAdmin = new NpgsqlConnection(firstConnectionString);
            await using var secondAdmin = new NpgsqlConnection(secondConnectionString);
            await Task.WhenAll(firstAdmin.OpenAsync(), secondAdmin.OpenAsync());
            var overlap = await BothMigrationsAreRunningAsync(firstAdmin, secondAdmin);
            await Task.WhenAll(runs);

            Assert.True(overlap);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (runs is not null) await Task.WhenAll(runs);
            }
            catch when (testFailure is not null)
            {
            }

            try
            {
                await second.DisposeAsync();
            }
            catch when (testFailure is not null)
            {
            }
        }
    }

    private static async Task RunAsync(MigrationRunner runner, string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await runner.RunAsync(connection);
    }

    private static async Task<bool> BothMigrationsAreRunningAsync(NpgsqlConnection first, NpgsqlConnection second)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var running = await Task.WhenAll(IsDelayedMigrationRunningAsync(first), IsDelayedMigrationRunningAsync(second));
            if (running.All(value => value)) return true;
            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<bool> IsDelayedMigrationRunningAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("select exists (select 1 from pg_stat_activity where datname = current_database() and state = 'active' and query like @query)", connection);
        command.Parameters.AddWithValue("query", "%pg_sleep(3)%");
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
