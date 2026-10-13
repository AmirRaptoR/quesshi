using Quesshi.Application;
using Quesshi.Features;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server.Commands;
using Quesshi.Server.Health;
using Quesshi.Server.Tenants;
using Npgsql;
using Orleans.Configuration;
using Orleans.Hosting;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Clock;
using System.Net;

namespace Quesshi.Server;

public static class Program
{
    public static Task<int> Main(string[] args) => DispatchAsync(args);

    internal static async Task<int> DispatchAsync(
        string[] args,
        Func<Task>? serve = null,
        Func<Task>? migrate = null,
        Func<Task<int>>? sync = null,
        Func<IReadOnlyList<string>, Task>? tenantAdd = null)
    {
        try
        {
            switch (args)
            {
                case ["migrate"]:
                    await (migrate ?? (() => MigrateAsync(Environment.GetEnvironmentVariable("ConnectionStrings__Owner"))))();
                    return 0;
                case ["sync"]:
                    await (sync ?? (() => SyncAsync()))();
                    return 0;
                case ["tenant", "add", .. var tenantArgs]:
                    await (tenantAdd ?? (async values => _ = await AddTenantAsync(values)))(tenantArgs);
                    return 0;
            case []:
            case ["serve"]:
                await (serve ?? (() => ServeAsync(args)))();
                return 0;
            default:
                throw new CommandException("Expected 'serve', 'migrate', 'sync', or 'tenant add'.");
            }
        }
        catch (CommandException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (InvalidOperationException exception)
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

    internal static WebApplication CreateApplication(
        DatabaseSources? configuredSources = null,
        int? siloPort = null,
        int? gatewayPort = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var options = new WebApplicationOptions { Args = [] };
        var builder = WebApplication.CreateBuilder(options);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddEnvironmentVariables();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        configureLogging?.Invoke(builder.Logging);

        var sources = configuredSources ?? DatabaseSources.FromEnvironment();
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton<IDatabaseClock, PostgresDatabaseClock>();
        builder.Services.AddSingleton<PostgresClock>();
        builder.Services.AddSingleton<IClock>(provider => provider.GetRequiredService<PostgresClock>());
        builder.Services.AddSingleton<IClockHealth>(provider => provider.GetRequiredService<PostgresClock>());
        builder.Services.AddSingleton<IBackgroundLoopRegistry, BackgroundLoopRegistry>();
        builder.Services.AddSingleton<IPlatformDatabaseHealth, PlatformDatabaseHealth>();
        builder.Services.AddSingleton<IPlatformSiloHealth, PlatformSiloHealth>();
        builder.Services.AddSingleton<PlatformHealth>();
        builder.Services.AddTenantResolution();
        builder.Host.UseOrleans(silo =>
        {
            var membership = new NpgsqlConnectionStringBuilder(sources.SystemConnectionString) { SearchPath = "orleans" }.ConnectionString;
            silo.Configure<ClusterOptions>(cluster => { cluster.ClusterId = "quesshi"; cluster.ServiceId = "quesshi"; });
            if (siloPort is not null && gatewayPort is not null)
            {
                silo.ConfigureEndpoints(IPAddress.Loopback, siloPort.Value, gatewayPort.Value, listenOnAnyHostAddress: false);
            }
            else
            {
                silo.ConfigureEndpoints(11111, 30000);
            }
            silo.UseAdoNetClustering(configuration =>
            {
                configuration.Invariant = "Npgsql";
                configuration.ConnectionString = membership;
            });
        });

        var app = builder.Build();
        app.MapGet("/health", async (PlatformHealth health, CancellationToken cancellationToken) =>
        {
            var report = await health.CheckAsync(cancellationToken);
            return report.IsHealthy ? Results.Ok(report) : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), tenant => tenant.UseTenantResolution());
        app.Use(async (context, next) =>
        {
            var tenant = TenantResolutionMiddleware.GetTenant(context);
            using (app.Logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenant_id"] = tenant?.Id,
                ["session_id"] = context.Items.TryGetValue("session_id", out var session) ? session : null
            })) await next();
        });
        return app;
    }

    private static async Task ServeAsync(string[] args)
    {
        await using var sources = DatabaseSources.FromEnvironment();
        await ServeAsync(sources);
    }

    internal static async Task ServeAsync(
        DatabaseSources sources,
        Func<WebApplication, Task>? run = null,
        CancellationToken cancellationToken = default)
    {
        var migrations = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly, typeof(FeaturesMarker).Assembly);
        await using (var connection = await sources.System.OpenConnectionAsync(cancellationToken))
        {
            await new MigrationRunner(migrations).EnsureCurrentAsync(connection, cancellationToken);
        }

        await using var app = CreateApplication(sources);
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        await (run ?? (application => application.RunAsync(cancellationToken)))(app);
    }
}
