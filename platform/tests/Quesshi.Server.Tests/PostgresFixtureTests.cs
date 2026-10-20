using Npgsql;
using Quesshi.Testing;
using Quesshi.Server;

namespace Quesshi.Server.Tests;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixtureConfigurationTests
{
    [Fact]
    public void Missing_connection_string_is_not_configured_outside_CI()
    {
        Assert.False(PostgresFixture.IsConfigured(null, isCi: false));
    }

    [Fact]
    public void Missing_connection_string_fails_in_CI()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PostgresFixture.IsConfigured(null, isCi: true));

        Assert.Contains(PostgresFixture.AdminConnectionStringEnvironmentVariable, exception.Message);
    }
}

public sealed class MigrateCommandTests
{
    [Fact]
    public async Task Migrate_requires_an_owner_connection_string()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Program.MigrateAsync(null));

        Assert.Contains("ConnectionStrings__Owner", exception.Message);
    }

    [Fact]
    public async Task Serve_does_not_require_an_owner_connection_string()
    {
        var served = false;
        await Program.DispatchAsync([], () => { served = true; return Task.CompletedTask; });

        Assert.True(served);
    }
}

[Collection("PostgreSQL")]
public sealed class PostgresFixtureIntegrationTests(PostgresFixture fixture)
{
    [SkippableFact]
    public void Provides_a_unique_database_for_the_collection()
    {
        Skip.If(fixture.ConnectionString is null, $"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run PostgreSQL tests locally.");

        Assert.NotNull(fixture.ConnectionString);
        Assert.StartsWith("quesshi_test_", fixture.DatabaseName);
    }
}

public sealed class PostgresFixtureIsolationTests
{
    [SkippableFact]
    public async Task Separate_fixtures_use_different_databases_and_do_not_share_committed_tenants()
    {
        var first = CreateConfiguredFixture();
        var second = CreateConfiguredFixture();
        var tenant = Guid.Parse("f7679852-11b2-4dc0-9be4-f9157c1d5a80");
        try
        {
            await first.InitializeAsync();
            await second.InitializeAsync();

            Assert.NotEqual(first.DatabaseName, second.DatabaseName);

            await Task.WhenAll(
                Program.MigrateAsync(first.ConnectionStringForRole("quesshi_owner")),
                Program.MigrateAsync(second.ConnectionStringForRole("quesshi_owner")));
            await InsertTenantAsync(first.ConnectionStringForRole("quesshi_owner"), tenant);

            Assert.False(await TenantExistsAsync(second.ConnectionStringForRole("quesshi_owner"), tenant));
        }
        finally
        {
            try
            {
                await second.DisposeAsync();
            }
            finally
            {
                await first.DisposeAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Concurrent_fixture_initialization_makes_both_databases_usable()
    {
        var first = CreateConfiguredFixture();
        var second = CreateConfiguredFixture();
        try
        {
            await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());

            var results = await Task.WhenAll(
                QueryOneAsync(first.ConnectionStringForRole("quesshi_owner")),
                QueryOneAsync(second.ConnectionStringForRole("quesshi_owner")));

            Assert.All(results, result => Assert.Equal(1, result));
        }
        finally
        {
            try
            {
                await second.DisposeAsync();
            }
            finally
            {
                await first.DisposeAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Disposing_one_fixture_drops_its_database_without_affecting_another_fixture()
    {
        var first = CreateConfiguredFixture();
        var second = CreateConfiguredFixture();
        try
        {
            await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
            var droppedDatabase = Assert.IsType<string>(first.DatabaseName);

            await first.DisposeAsync();

            Assert.False(await DatabaseExistsAsync(droppedDatabase));
            Assert.Equal(1, await QueryOneAsync(second.ConnectionStringForRole("quesshi_owner")));
        }
        finally
        {
            try
            {
                await second.DisposeAsync();
            }
            finally
            {
                await first.DisposeAsync();
            }
        }
    }

    private static PostgresFixture CreateConfiguredFixture()
    {
        var fixture = new PostgresFixture();
        Skip.If(!PostgresFixture.IsConfigured(Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable), isCi: false),
            $"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run PostgreSQL tests locally.");
        return fixture;
    }

    private static async Task InsertTenantAsync(string connectionString, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("insert into tenants (id, slug, name, default_language) values (@tenant, 'fixture-isolation', 'Fixture isolation', 'en')", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TenantExistsAsync(string connectionString, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select exists(select 1 from tenants where id = @tenant)", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> QueryOneAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select 1", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select exists(select 1 from pg_database where datname = @database)", connection);
        command.Parameters.AddWithValue("database", databaseName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}

public sealed class FirstIsolatedPostgresFixtureClass(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task First_class_fixture_runs_concurrently_without_second_class_state()
    {
        Skip.If(fixture.ConnectionString is null, $"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run PostgreSQL tests locally.");

        Assert.False(await IsolatedClassFixtureCoordinator.WriteTenantAndObservePeerAsync(fixture, isFirst: true));
    }
}

public sealed class SecondIsolatedPostgresFixtureClass(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Second_class_fixture_runs_concurrently_without_first_class_state()
    {
        Skip.If(fixture.ConnectionString is null, $"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run PostgreSQL tests locally.");

        Assert.False(await IsolatedClassFixtureCoordinator.WriteTenantAndObservePeerAsync(fixture, isFirst: false));
    }
}

internal static class IsolatedClassFixtureCoordinator
{
    private static readonly Guid FirstTenant = Guid.Parse("b8ac111b-aa71-4f78-9d5f-7c90404330f8");
    private static readonly Guid SecondTenant = Guid.Parse("86db0a4f-676d-4088-a1ca-4b98362e538a");
    private static readonly TaskCompletionSource FirstMigration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource SecondMigration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource FirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource SecondWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<bool> WriteTenantAndObservePeerAsync(PostgresFixture fixture, bool isFirst)
    {
        var connectionString = fixture.ConnectionStringForRole("quesshi_owner");
        await Program.MigrateAsync(connectionString);
        await WaitForPeerAsync(isFirst ? FirstMigration : SecondMigration, isFirst ? SecondMigration : FirstMigration);

        var tenant = isFirst ? FirstTenant : SecondTenant;
        var peerTenant = isFirst ? SecondTenant : FirstTenant;
        await InsertTenantAsync(connectionString, tenant);
        await WaitForPeerAsync(isFirst ? FirstWrite : SecondWrite, isFirst ? SecondWrite : FirstWrite);

        return await TenantExistsAsync(connectionString, peerTenant);
    }

    private static async Task WaitForPeerAsync(TaskCompletionSource current, TaskCompletionSource peer)
    {
        current.TrySetResult();
        await peer.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task InsertTenantAsync(string connectionString, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("insert into tenants (id, slug, name, default_language) values (@tenant, @slug, 'Class fixture isolation', 'en')", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("slug", $"class-fixture-{tenant:N}");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TenantExistsAsync(string connectionString, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select exists(select 1 from tenants where id = @tenant)", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
