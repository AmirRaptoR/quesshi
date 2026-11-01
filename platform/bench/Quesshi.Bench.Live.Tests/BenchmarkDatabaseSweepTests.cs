using Npgsql;
using Quesshi.Bench.Live;
using Quesshi.Testing;

namespace Quesshi.Bench.Live.Tests;

[Collection("BenchmarkDatabaseSweep")]
public sealed class BenchmarkDatabaseSweepTests
{
    private static readonly string? Admin = Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(2);

    [SkippableFact]
    public async Task Sweep_drops_old_bench_databases_and_leaves_test_young_and_busy_ones()
    {
        Skip.If(string.IsNullOrWhiteSpace(Admin), "PostgreSQL is not configured.");
        var old = await CreateAsync("quesshi_bench_sweep_old_");
        var busy = await CreateAsync("quesshi_bench_sweep_busy_");
        var test = await CreateAsync("quesshi_test_sweep_bench_");
        await using var held = await OpenAsync(busy);
        // Held open so a concurrent PostgresFixture sweep in another test process cannot drop it.
        await using var heldTest = await OpenAsync(test);
        await Task.Delay(Threshold + TimeSpan.FromSeconds(1.5));
        var young = await CreateAsync("quesshi_bench_sweep_young_");
        try
        {
            await LiveBenchmarkRunner.SweepStaleDatabasesAsync(Admin!, Threshold);

            Assert.False(await ExistsAsync(old));
            Assert.True(await ExistsAsync(busy));
            Assert.True(await ExistsAsync(test));
            Assert.True(await ExistsAsync(young));
        }
        finally
        {
            await held.CloseAsync();
            await heldTest.CloseAsync();
            foreach (var name in new[] { old, busy, test, young }) await DropAsync(name);
        }
    }

    [SkippableFact]
    public async Task Sweep_ignores_an_unreachable_server()
    {
        await LiveBenchmarkRunner.SweepStaleDatabasesAsync("Host=127.0.0.1;Port=1;Timeout=1", Threshold);
    }

    private static async Task<string> CreateAsync(string prefix)
    {
        var name = $"{prefix}{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return name;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string database)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin) { Database = database, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task DropAsync(string name)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
