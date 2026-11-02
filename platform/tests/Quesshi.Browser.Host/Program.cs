using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Testing;

// Runs the real platform server against a throw-away PostgreSQL database for the Playwright browser tests: one
// tenant with the async_duel format enabled and one registered player whose email sign-in code the server logs.
// It prints one "QUESSHI_BROWSER_HOST_READY {json}" line once listening and stops when standard input closes.
const string TenantHost = "real.localhost";
const string PlayerEmail = "ada@example.test";

var postgres = new PostgresFixture();
await postgres.InitializeAsync();
if (postgres.ConnectionString is null)
{
    await Console.Error.WriteLineAsync($"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run the real server for browser tests.");
    return 2;
}

try
{
    await Quesshi.Server.Program.MigrateAsync(postgres.ConnectionStringForRole("quesshi_owner"));
    var sources = new DatabaseSources(postgres.ConnectionStringForRole("quesshi_app"), postgres.ConnectionStringForRole("quesshi_system"));
    var tenantId = await Quesshi.Server.Program.AddTenantAsync(
        ["--slug", "browser", "--name", "Browser", "--host", TenantHost, "--default-language", "en", "--language", "en:English:English:ltr"],
        sources);
    await using (var owner = new NpgsqlConnection(postgres.ConnectionStringForRole("quesshi_owner")))
    {
        await owner.OpenAsync();
        await owner.ExecuteAsync("""
            update tenant_capabilities set entitled = true, enabled = true
            where tenant_id = @tenantId and (type, key) in (('format', 'async_duel'), ('kind', 'choice'), ('source', 'bank'));
            with account as (insert into accounts(id, email) values (gen_random_uuid(), @email) returning id)
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            select @tenantId, gen_random_uuid(), id, 'Ada', 'avatar-7', 'en' from account;
            """, new { tenantId, email = PlayerEmail });
    }

    var application = Quesshi.Server.Program.CreateApplication(sources, FreePort(), FreePort(), environmentName: "Development");
    application.Urls.Add("http://127.0.0.1:0");
    await application.StartAsync();
    await application.Services.GetRequiredService<PostgresClock>().StartAsync();
    var port = new Uri(application.Urls.Single()).Port;
    Console.WriteLine($"QUESSHI_BROWSER_HOST_READY {JsonSerializer.Serialize(new { port, tenantHost = TenantHost, email = PlayerEmail })}");
    await Console.In.ReadToEndAsync();
    await application.StopAsync();
    await application.DisposeAsync();
    return 0;
}
finally
{
    await postgres.DisposeAsync();
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
