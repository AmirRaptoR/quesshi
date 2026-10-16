using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Quesshi.Server.Tenants;
using Quesshi.Testing;
using System.Collections.Concurrent;
using System.Text.Json;

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
        await Assert.ThrowsAsync<PostgresException>(() => system.ExecuteAsync("insert into orleans.orleansquery(querykey, querytext) values ('forbidden', 'select 1')"));
        await Assert.ThrowsAsync<PostgresException>(() => system.ExecuteAsync("update orleans.orleansquery set querytext = 'select 1' where querykey = 'UpdateIAmAlivetimeKey'"));
        await Assert.ThrowsAsync<PostgresException>(() => system.ExecuteAsync("delete from orleans.orleansquery where querykey = 'UpdateIAmAlivetimeKey'"));

        await using var app = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_app"));
        await app.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteScalarAsync<long>("select count(*) from orleans.orleansquery"));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("select * from orleans.insert_membership_version('permission-test')"));
    }

    [SkippableFact]
    public async Task Membership_compare_and_swap_rolls_back_rows_and_versions_on_failure()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        await using var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionStringForRole("quesshi_owner"))
        {
            SearchPath = "orleans"
        }.ConnectionString);
        await owner.OpenAsync();
        const string deployment = "membership-cas-test";
        await owner.ExecuteAsync("select * from orleans.insert_membership_version(@deployment)", new { deployment });

        var args = new
        {
            deployment,
            address = "127.0.0.1",
            port = 11111,
            generation = 1,
            siloName = "cas-silo",
            hostName = "localhost",
            status = 3,
            proxyPort = 30000,
            startTime = DateTimeOffset.UtcNow,
            aliveTime = DateTimeOffset.UtcNow
        };
        const string insert = """
            select * from orleans.insert_membership(
                @deployment, @address, @port, @generation, @siloName, @hostName, @status, @proxyPort, @startTime, @aliveTime, @version)
            """;

        Assert.Equal(0, await owner.ExecuteScalarAsync<int>(insert, new { args.deployment, args.address, args.port, args.generation, args.siloName, args.hostName, args.status, args.proxyPort, args.startTime, args.aliveTime, version = -1 }));
        Assert.Equal(0L, await owner.ExecuteScalarAsync<long>("select count(*) from orleans.orleansmembershiptable where deploymentid = @deployment", new { deployment }));
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>("select version from orleans.orleansmembershipversiontable where deploymentid = @deployment", new { deployment }));

        Assert.Equal(1, await owner.ExecuteScalarAsync<int>(insert, new { args.deployment, args.address, args.port, args.generation, args.siloName, args.hostName, args.status, args.proxyPort, args.startTime, args.aliveTime, version = 0 }));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>("select version from orleans.orleansmembershipversiontable where deploymentid = @deployment", new { deployment }));

        Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
            select * from orleans.update_membership(@deployment, '127.0.0.2', 11112, 2, 3, null, @aliveTime, 1)
            """, new { deployment, args.aliveTime }));
        Assert.Equal(1, await owner.ExecuteScalarAsync<int>("select version from orleans.orleansmembershipversiontable where deploymentid = @deployment", new { deployment }));
    }

    [SkippableFact]
    public async Task Runtime_membership_role_rejects_pending_migrations_before_silo_startup()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var migrations = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly);
        var expected = migrations.Single(migration => migration.Name == "20261013T0200_core_orleans.sql");
        var pending = migrations.Where(migration => string.CompareOrdinal(migration.Name, expected.Name) >= 0).ToArray();

        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        var membershipRowsBefore = await owner.ExecuteScalarAsync<long>("select count(*) from orleans.orleansmembershiptable where deploymentid = 'quesshi'");
        await owner.ExecuteAsync("delete from public.schema_migrations where name = any(@names)", new { names = pending.Select(migration => migration.Name).ToArray() });
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Program.ServeAsync(CreateSources()));
            Assert.Contains("pending migration '20261013T0200_core_orleans.sql'", exception.Message);
            Assert.Contains("ConnectionStrings__Owner", exception.Message);
            Assert.Equal(membershipRowsBefore, await owner.ExecuteScalarAsync<long>("select count(*) from orleans.orleansmembershiptable where deploymentid = 'quesshi'"));
        }
        finally
        {
            foreach (var migration in pending)
            {
                await owner.ExecuteAsync(
                    "insert into public.schema_migrations(name, owner, checksum) values (@name, @owner, @checksum)",
                    new { name = migration.Name, owner = migration.Owner, checksum = migration.Checksum });
            }
        }
    }

    [SkippableFact]
    public async Task Two_silos_join_the_cluster_and_ping_across_silos()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        await using var first = Program.CreateApplication(CreateSources(), 11123, 30123);
        await using var second = Program.CreateApplication(CreateSources(), 11124, 30124);
        first.Urls.Add("http://127.0.0.1:0");
        second.Urls.Add("http://127.0.0.1:0");
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
            await WaitUntilAsync(
                async () => (await client.GetAsync("/health")).StatusCode == System.Net.HttpStatusCode.OK,
                "health did not become ready after the background loops completed their first pass");

            var loop = app.Services.GetRequiredService<IBackgroundLoopRegistry>().Register("test-loop");
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
            loop.Passed();
            var healthy = await client.GetAsync("/health");
            Assert.Equal(System.Net.HttpStatusCode.OK, healthy.StatusCode);
            using var report = JsonDocument.Parse(await healthy.Content.ReadAsStringAsync());
            var root = report.RootElement;
            Assert.True(root.GetProperty("database").GetBoolean());
            Assert.True(root.GetProperty("clock").GetBoolean());
            Assert.True(root.GetProperty("silo").GetBoolean());
            var loopReport = root.GetProperty("loops").EnumerateArray()
                .Single(loop => loop.GetProperty("name").GetString() == "test-loop");
            Assert.True(TimeSpan.Parse(loopReport.GetProperty("lastSuccessfulPassAge").GetString()!) < TimeSpan.FromSeconds(30));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Composed_host_keeps_health_public_and_applies_tenant_proxy_and_logging_scope()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var sources = CreateSources();
        var tenantId = await Program.AddTenantAsync([
            "--slug", "host-test", "--name", "Host test", "--host", "tenant.example.test",
            "--default-language", "en", "--language", "en:English:English:ltr"], sources);

        var originalTrustedProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXIES");
        Environment.SetEnvironmentVariable("TRUSTED_PROXIES", "127.0.0.1");
        var logs = new CapturingLoggerProvider();
        await using var app = Program.CreateApplication(sources, 11126, 30126, logging => logging.AddProvider(logs));
        app.MapGet("/tenant-probe", (HttpContext context, ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger("tenant-probe").LogInformation("Tenant probe");
            return Results.Ok(TenantResolutionMiddleware.GetTenant(context)?.Slug);
        });
        app.Urls.Add("http://127.0.0.1:50126");
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50126") };
            using var publicHealth = new HttpRequestMessage(HttpMethod.Get, "/health");
            publicHealth.Headers.Host = "no-tenant.example.test";
            Assert.NotEqual(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(publicHealth)).StatusCode);

            using var unknownTenant = new HttpRequestMessage(HttpMethod.Get, "/tenant-probe");
            unknownTenant.Headers.Host = "no-tenant.example.test";
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(unknownTenant)).StatusCode);

            using var tenantRequest = new HttpRequestMessage(HttpMethod.Get, "/tenant-probe");
            tenantRequest.Headers.Host = "proxy.example.test";
            tenantRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", "tenant.example.test");
            var tenantResponse = await client.SendAsync(tenantRequest);
            Assert.Equal(System.Net.HttpStatusCode.OK, tenantResponse.StatusCode);
            Assert.Equal("\"host-test\"", await tenantResponse.Content.ReadAsStringAsync());

            var entry = Assert.Single(logs.Entries, entry => entry.Message == "Tenant probe");
            var scope = Assert.Single(entry.Scopes.OfType<IReadOnlyDictionary<string, object?>>());
            Assert.Equal(tenantId, Assert.IsType<Guid>(scope["tenant_id"]));
            Assert.True(scope.ContainsKey("session_id"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRUSTED_PROXIES", originalTrustedProxies);
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failureMessage)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            if (await condition()) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.Fail(failureMessage);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        public ConcurrentQueue<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes?.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<object>();
                provider._scopes?.ForEachScope((scope, collected) => collected.Add(scope!), scopes);
                provider.Entries.Enqueue(new LogEntry(formatter(state, exception), scopes));
            }
        }
    }

    private sealed record LogEntry(string Message, IReadOnlyList<object> Scopes);
}
