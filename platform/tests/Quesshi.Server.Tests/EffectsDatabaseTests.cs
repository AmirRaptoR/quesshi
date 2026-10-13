using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class EffectsDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("53b35940-77f1-4f60-8cf9-ae24f8ecf5de");
    private static readonly Guid TenantB = Guid.Parse("b9627d2b-5129-4c6d-b642-ae9cdbb4c5d4");

    [Fact]
    public void Effects_migration_follows_the_session_storage_migration()
    {
        var names = MigrationRunner.Discover(typeof(ApplicationMarker).Assembly).Select(migration => migration.Name).ToArray();

        Assert.Contains("20261013T0103_core_effects.sql", names);
        Assert.True(Array.IndexOf(names, "20261013T0102_core_sessions.sql") < Array.IndexOf(names, "20261013T0103_core_effects.sql"));
    }

    [SkippableFact]
    public async Task Effects_tables_are_tenant_isolated_and_cleanup_jobs_can_retry_without_limit()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        await EnsureTenantAsync(owner, TenantA);
        await EnsureTenantAsync(owner, TenantB);

        Assert.True(await ScalarAsync<bool>(owner, "select to_regclass('plugin_effects') is not null"));
        Assert.True(await ScalarAsync<bool>(owner, "select to_regclass('session_effects') is not null"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from pg_indexes where indexname = 'plugin_effects_age'"));

        await using var appA = await OpenAsync("quesshi_app");
        await SetTenantAsync(appA, TenantA);
        await ExecuteAsync(appA, "insert into plugin_effects (tenant_id, plugin, event_key) values (@tenant, 'sample', 'sessionEnded:one')", ("tenant", TenantA));
        await ExecuteAsync(appA, "select enqueue_job('core', 'cleanup-player', '{}'::jsonb, 'cleanup', now(), null)");

        await using var appB = await OpenAsync("quesshi_app");
        await SetTenantAsync(appB, TenantB);
        Assert.Equal(0L, await ScalarAsync<long>(appB, "select count(*) from plugin_effects"));

        Assert.True(await ScalarAsync<bool>(owner, "select max_attempts is null from jobs where tenant_id = @tenant and kind = 'cleanup-player'", ("tenant", TenantA)));
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task EnsureTenantAsync(NpgsqlConnection connection, Guid tenant) =>
        await ExecuteAsync(connection, """
            insert into tenants (id, slug, name, default_language)
            values (@tenant, @slug, @slug, 'en')
            on conflict (id) do nothing
            """, ("tenant", tenant), ("slug", $"effects-{tenant:N}"));

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenant) =>
        ExecuteAsync(connection, "select set_config('app.tenant_id', @tenant, false)", ("tenant", tenant.ToString()));

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? default! : (T)result!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
