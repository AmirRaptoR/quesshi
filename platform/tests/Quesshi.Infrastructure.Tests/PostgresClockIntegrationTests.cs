using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Testing;

namespace Quesshi.Infrastructure.Tests;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

[Collection("PostgreSQL")]
public sealed class PostgresClockIntegrationTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Calibration_reads_PostgreSQL_clock_timestamp()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        await using var clock = new PostgresClock(new PostgresDatabaseClock(sources));

        Assert.True(await clock.CalibrateAsync());

        Assert.True(clock.HasCalibration);
        Assert.NotNull(clock.Uncertainty);
        Assert.True(clock.IsHealthy);
    }
}
