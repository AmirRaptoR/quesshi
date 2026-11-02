using Npgsql;
using Quesshi.Testing;

namespace Quesshi.Infrastructure.Tests;

// Runs alone (see PostgresCollection): the small threshold would otherwise also sweep idle databases of parallel classes.
[Collection("PostgreSQL")]
public sealed class PostgresFixtureSweepTests
{
    private static readonly string? Admin = Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(2);

    [SkippableFact]
    public async Task Start_drops_an_old_test_database_and_keeps_a_young_one_with_an_open_connection()
    {
        Skip.If(string.IsNullOrWhiteSpace(Admin), "PostgreSQL is not configured.");
        await using var sweepLock = await LockSweepWindowAsync();
        var old = await CreateAsync("quesshi_test_sweep_old_");
        await Task.Delay(Threshold + TimeSpan.FromSeconds(1.5));
        var young = await CreateAsync("quesshi_test_sweep_young_");
        await using var held = await OpenAsync(young);
        var fixture = new PostgresFixture(Admin, isCi: false, Threshold);
        try
        {
            await fixture.InitializeAsync();

            Assert.False(await ExistsAsync(old));
            Assert.True(await ExistsAsync(young));
        }
        finally
        {
            await fixture.DisposeAsync();
            await held.CloseAsync();
            await DropAsync(old);
            await DropAsync(young);
        }
    }

    [SkippableFact]
    public async Task Start_leaves_an_old_test_database_that_still_has_a_connection_and_still_succeeds()
    {
        Skip.If(string.IsNullOrWhiteSpace(Admin), "PostgreSQL is not configured.");
        await using var sweepLock = await LockSweepWindowAsync();
        var busy = await CreateAsync("quesshi_test_sweep_busy_");
        await using var held = await OpenAsync(busy);
        await Task.Delay(Threshold + TimeSpan.FromSeconds(1.5));
        var fixture = new PostgresFixture(Admin, isCi: false, Threshold);
        try
        {
            await fixture.InitializeAsync();

            Assert.NotNull(fixture.ConnectionString);
            Assert.True(await ExistsAsync(busy));
        }
        finally
        {
            await fixture.DisposeAsync();
            await held.CloseAsync();
            await DropAsync(busy);
        }
    }

    [SkippableFact]
    public async Task Start_only_sweeps_databases_named_quesshi_test()
    {
        Skip.If(string.IsNullOrWhiteSpace(Admin), "PostgreSQL is not configured.");
        await using var sweepLock = await LockSweepWindowAsync();
        var bench = await CreateAsync("quesshi_bench_sweep_other_");
        var other = await CreateAsync("quesshi_other_sweep_");
        await Task.Delay(Threshold + TimeSpan.FromSeconds(1.5));
        var fixture = new PostgresFixture(Admin, isCi: false, Threshold);
        try
        {
            await fixture.InitializeAsync();

            Assert.True(await ExistsAsync(bench));
            Assert.True(await ExistsAsync(other));
        }
        finally
        {
            await fixture.DisposeAsync();
            await DropAsync(bench);
            await DropAsync(other);
        }
    }

    // Both sweep-test classes run with a 2-second threshold in separate test processes; this cross-process lock keeps one
    // from dropping the other's cross-prefix database, so those databases need no open connection to survive.
    private static async Task<NpgsqlConnection> LockSweepWindowAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Admin) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(476001)", connection);
        await command.ExecuteNonQueryAsync();
        return connection;
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
