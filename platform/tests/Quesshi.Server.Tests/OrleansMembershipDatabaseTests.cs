using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans;
using Orleans.Runtime;
using Quesshi.Application;
using Quesshi.Features;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server.Health;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class OrleansMembershipDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Migration_installs_membership_only_in_orleans_with_runtime_permissions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        Assert.Equal("orleans.orleansmembershiptable", await owner.ExecuteScalarAsync<string>("select to_regclass('orleans.\"orleansmembershiptable\"')::text"));
        Assert.Null(await owner.ExecuteScalarAsync<string?>("select to_regclass('public.\"orleansmembershiptable\"')::text"));
        Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from public.schema_migrations where name = '20261013T0200_core_orleans.sql'"));

        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        Assert.Equal(1L, await owner.ExecuteScalarAsync<long>("select count(*) from public.schema_migrations where name = '20261013T0200_core_orleans.sql'"));

        await using var system = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionStringForRole("quesshi_system"))
        {
            SearchPath = "orleans"
        }.ConnectionString);
        await system.OpenAsync();
        Assert.Equal(9L, await system.ExecuteScalarAsync<long>("select count(*) from orleans.orleansquery"));
        await system.ExecuteAsync("select * from orleans.insert_membership_version('permission-test')");

        await using var app = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_app"));
        await app.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteScalarAsync<long>("select count(*) from orleans.orleansquery"));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("select * from orleans.insert_membership_version('permission-test')"));
    }

    [SkippableFact]
    public async Task Runtime_membership_role_rejects_pending_migrations_before_silo_startup()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var migrations = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly);
        var expected = migrations.Single(migration => migration.Name == "20261013T0200_core_orleans.sql");

        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await owner.ExecuteAsync("delete from public.schema_migrations where name = @name", new { name = expected.Name });
        try
        {
            await using var system = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionStringForRole("quesshi_system"))
            {
                SearchPath = "orleans"
            }.ConnectionString);
            await system.OpenAsync();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new MigrationRunner(migrations).EnsureCurrentAsync(system));
            Assert.Contains("pending migration '20261013T0200_core_orleans.sql'", exception.Message);
            Assert.Contains("ConnectionStrings__Owner", exception.Message);
        }
        finally
        {
            await owner.ExecuteAsync(
                "insert into public.schema_migrations(name, owner, checksum) values (@name, @owner, @checksum)",
                new { name = expected.Name, owner = expected.Owner, checksum = expected.Checksum });
        }
    }

    [SkippableFact]
    public async Task Two_silos_join_the_cluster_and_ping_across_silos()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        await using var first = Program.CreateApplication(CreateSources(), 11123, 30123);
        await using var second = Program.CreateApplication(CreateSources(), 11124, 30124);
        first.Urls.Add("http://127.0.0.1:50123");
        second.Urls.Add("http://127.0.0.1:50124");
        await first.StartAsync();
        await second.StartAsync();

        try
        {
            await WaitForActiveSilosAsync(2);
            var factory = second.Services.GetRequiredService<IGrainFactory>();
            var localSilo = second.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToParsableString();
            var crossedSilos = false;
            for (var index = 0; index < 32; index++)
            {
                var pong = await factory.GetGrain<IPingGrain>($"ping-{index}-{Guid.NewGuid():N}").PingAsync().WaitAsync(TimeSpan.FromSeconds(10));
                crossedSilos |= !string.Equals(localSilo, pong, StringComparison.Ordinal);
            }

            Assert.True(crossedSilos, "At least one test-only grain activation should run on the other silo.");
        }
        finally
        {
            await second.StopAsync();
            await first.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Health_reflects_calibration_silo_and_registered_loop_state()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        await using var app = Program.CreateApplication(CreateSources(), 11125, 30125);
        app.Urls.Add("http://127.0.0.1:50125");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50125") };
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);

            await app.Services.GetRequiredService<PostgresClock>().StartAsync();
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

            var loop = app.Services.GetRequiredService<IBackgroundLoopRegistry>().Register("test-loop");
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
            loop.Passed();
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private DatabaseSources CreateSources() => new(
        fixture.ConnectionStringForRole("quesshi_app"),
        fixture.ConnectionStringForRole("quesshi_system"));

    private async Task WaitForActiveSilosAsync(int expected)
    {
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            var active = await owner.ExecuteScalarAsync<long>("select count(*) from orleans.orleansmembershiptable where deploymentid = 'quesshi' and status = 3");
            if (active >= expected) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Expected {expected} active silos in PostgreSQL membership.");
    }
}
