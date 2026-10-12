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
        await Program.Main([]);
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
