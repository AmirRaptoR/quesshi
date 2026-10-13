using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Database;
using Quesshi.Server;
using Quesshi.Server.Commands;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class CapabilitySyncDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Sync_provisions_only_missing_rows_as_not_entitled_and_is_idempotent()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        await SeedAsync(owner, "a");
        await SeedAsync(owner, "b");
        await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled) select id, 'switch', 'guests', true, true from tenants where slug = 'a'");

        var registry = CoreCapabilities.CreateRegistry();
        await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var command = new CapabilitySyncCommand(new SystemDb(sources), registry);

        Assert.Equal(3, await command.ExecuteAsync());
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select config_version from tenants where slug = 'a'"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select config_version from tenants where slug = 'b'"));
        Assert.Equal(0, await command.ExecuteAsync());
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select config_version from tenants where slug = 'a'"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select config_version from tenants where slug = 'b'"));
        Assert.Equal(4L, await ScalarAsync<long>(owner, "select count(*) from tenant_capabilities"));
        Assert.True(await ScalarAsync<bool>(owner, "select entitled and enabled from tenant_capabilities where type = 'switch' and key = 'guests' and tenant_id = (select id from tenants where slug = 'a')"));
        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from tenant_capabilities where not entitled and not enabled"));
    }

    private static async Task SeedAsync(NpgsqlConnection owner, string slug)
    {
        var tenant = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@tenant, @slug, @slug, 'en')", ("tenant", tenant), ("slug", slug));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
