using Npgsql;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class TenantConfigurationDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("1e7d9415-30ac-4cce-9e3f-5cb949cd2d6e");
    private static readonly Guid TenantB = Guid.Parse("a487fa84-7cd6-4f4e-8d8b-624ce7536844");
    private static readonly IReadOnlySet<string> GlobalTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "tenants", "tenant_hosts", "tenant_languages", "tenant_capabilities"
    };
    private static readonly IReadOnlySet<string> CatalogueGlobalTables = new HashSet<string>(GlobalTables, StringComparer.Ordinal)
    {
        "accounts", "jobs"
    };

    [SkippableFact]
    public async Task Configuration_migration_creates_the_documented_contract()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await MigrateAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await SeedAsync(owner);

        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_indexes where tablename = 'tenant_hosts' and indexname = 'tenant_hosts_primary')"));
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_indexes where tablename = 'tenant_hosts' and indexname = 'tenant_hosts_tenant')"));
        var primaryHostFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "insert into tenant_hosts (host, tenant_id, is_primary) values ('second-a.example.test', @tenant, true)", ("tenant", TenantA)));
        Assert.Equal("23505", primaryHostFailure.SqlState);
        var hostFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "insert into tenant_hosts (host, tenant_id) values ('UPPER.example.test', @tenant)", ("tenant", TenantA)));
        Assert.Equal("23514", hostFailure.SqlState);
        foreach (var invalidHost in new[] { "example.test.", "a.example.test:8080", "a.example.test/path" })
        {
            var invalidHostFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "insert into tenant_hosts (host, tenant_id) values (@host, @tenant)", ("host", invalidHost), ("tenant", TenantA)));
            Assert.Equal("23514", invalidHostFailure.SqlState);
        }
        var slugFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@id, 'invalid slug', 'Invalid', 'en')", ("id", Guid.NewGuid())));
        Assert.Equal("23514", slugFailure.SqlState);
        var languageFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name, direction) values (@tenant, 'de', 'Deutsch', 'German', 'sideways')", ("tenant", TenantA)));
        Assert.Equal("23514", languageFailure.SqlState);
        var capabilityFailure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key) values (@tenant, 'invalid', 'demo')", ("tenant", TenantA)));
        Assert.Equal("23514", capabilityFailure.SqlState);
        Assert.Equal("en", await ScalarAsync<string>(owner, "select default_language from tenants where id = @tenant", ("tenant", TenantA)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from tenant_languages where tenant_id = @tenant and code = (select default_language from tenants where id = @tenant)", ("tenant", TenantA)));
        Assert.Equal("UTC", await ScalarAsync<string>(owner, "select timezone from tenants where id = @tenant", ("tenant", TenantA)));
        Assert.Equal("{}", await ScalarAsync<string>(owner, "select branding::text from tenants where id = @tenant", ("tenant", TenantA)));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select config_version from tenants where id = @tenant", ("tenant", TenantA)));
        Assert.Equal("ltr", await ScalarAsync<string>(owner, "select direction from tenant_languages where tenant_id = @tenant and code = 'en'", ("tenant", TenantA)));
        Assert.Equal(0, await ScalarAsync<int>(owner, "select sort_order from tenant_languages where tenant_id = @tenant and code = 'en'", ("tenant", TenantA)));
        Assert.True(await ScalarAsync<bool>(owner, "select entitled and enabled and settings = '{}'::jsonb from tenant_capabilities where tenant_id = @tenant", ("tenant", TenantA)));
    }

    [SkippableFact]
    public async Task Configuration_tables_enforce_app_role_grants_and_row_security()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await MigrateAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await SeedAsync(owner);
        await using var app = await OpenAsync("quesshi_app");
        await ExecuteAsync(app, "select set_config('app.tenant_id', @tenant::text, false)", ("tenant", TenantA));

        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from tenants"));
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from tenant_hosts"));
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from tenant_languages"));
        Assert.Equal(1L, await ScalarAsync<long>(app, "select count(*) from tenant_capabilities"));
        Assert.Equal(0L, await ExecuteAsync(app, "update tenants set name = 'changed' where id = @tenant", ("tenant", TenantB)));
        await ExecuteAsync(app, "update tenants set name = 'changed' where id = @tenant", ("tenant", TenantA));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "update tenants set slug = 'forbidden' where id = @tenant", ("tenant", TenantA)));
        await ExecuteAsync(app, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'nl', 'Nederlands', 'Dutch')", ("tenant", TenantA));
        await ExecuteAsync(app, "update tenant_languages set name = 'Nederlands (NL)' where tenant_id = @tenant and code = 'nl'", ("tenant", TenantA));
        await ExecuteAsync(app, "delete from tenant_languages where tenant_id = @tenant and code = 'nl'", ("tenant", TenantA));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'fa', 'فارسی', 'Persian')", ("tenant", TenantB)));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "update tenant_hosts set is_primary = false where tenant_id = @tenant", ("tenant", TenantA)));
        await ExecuteAsync(app, "update tenant_capabilities set enabled = false, settings = '{\"answer\":42}' where tenant_id = @tenant", ("tenant", TenantA));
        Assert.Equal(0L, await ExecuteAsync(app, "update tenant_capabilities set enabled = false where tenant_id = @tenant", ("tenant", TenantB)));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "update tenant_capabilities set entitled = false where tenant_id = @tenant", ("tenant", TenantA)));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "insert into tenant_capabilities (tenant_id, type, key) values (@tenant, 'feature', 'new')", ("tenant", TenantA)));
        await AssertInsufficientPrivilegeAsync(() => ExecuteAsync(app, "delete from tenant_capabilities where tenant_id = @tenant", ("tenant", TenantA)));

        await using var unbound = await OpenAsync("quesshi_app");
        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<long>(unbound, "select count(*) from tenants"));
    }

    [SkippableFact]
    public async Task System_role_can_operate_global_configuration_but_not_tenant_owned_tables()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await MigrateAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await SeedAsync(owner);
        await using var system = await OpenAsync("quesshi_system");
        foreach (var table in GlobalTables)
        {
            Assert.True(await ScalarAsync<bool>(system, "select has_table_privilege(current_user, @table, 'select,insert,update,delete')", ("table", table)));
            Assert.True(await ScalarAsync<long>(system, $"select count(*) from {table}") > 0);
        }

        await TenantIsolationTestHelper.AssertTenantTableAsync(fixture, "configuration_isolation_probe", TenantA, TenantB);
    }

    [SkippableFact]
    public async Task Tenant_catalogue_classifies_global_configuration_and_rejects_unisolated_tables()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await MigrateAsync();
        await using var owner = await OpenAsync("quesshi_owner");
        await SeedAsync(owner);
        await ExecuteAsync(owner, "drop table if exists catalogue_probe cascade");
        await ExecuteAsync(owner, "drop table if exists catalogue_missing_policy cascade");
        await ExecuteAsync(owner, "drop table if exists catalogue_wrong_key cascade");
        try
        {
            await ExecuteAsync(owner, "create table catalogue_probe (tenant_id uuid not null, id uuid not null, primary key (tenant_id, id))");
            await ExecuteAsync(owner, "select quesshi_tenant_table('catalogue_probe', 'test')");
            await TenantIsolationTestHelper.AssertTenantCatalogueAsync(owner, CatalogueGlobalTables);

            await ExecuteAsync(owner, "create table catalogue_missing_policy (tenant_id uuid not null, id uuid not null, primary key (tenant_id, id))");
            var policyFailure = await Assert.ThrowsAnyAsync<XunitException>(() => TenantIsolationTestHelper.AssertTenantCatalogueAsync(owner, CatalogueGlobalTables));
            Assert.Contains("catalogue_missing_policy", policyFailure.Message);
            await ExecuteAsync(owner, "drop table catalogue_missing_policy");
            await ExecuteAsync(owner, "create table catalogue_wrong_key (tenant_id uuid not null, id uuid primary key)");
            var keyFailure = await Assert.ThrowsAnyAsync<XunitException>(() => TenantIsolationTestHelper.AssertTenantCatalogueAsync(owner, CatalogueGlobalTables));
            Assert.Contains("catalogue_wrong_key", keyFailure.Message);
        }
        finally
        {
            await ExecuteAsync(owner, "drop table if exists catalogue_missing_policy cascade");
            await ExecuteAsync(owner, "drop table if exists catalogue_wrong_key cascade");
            await ExecuteAsync(owner, "drop table if exists catalogue_probe cascade");
        }
    }

    private async Task MigrateAsync() => await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

    private async Task SeedAsync(NpgsqlConnection owner)
    {
        await ExecuteAsync(owner, "delete from sessions; delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        foreach (var (tenant, slug, host) in new[] { (TenantA, "tenant-a", "a.example.test"), (TenantB, "tenant-b", "b.example.test") })
        {
            await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@id, @slug, @slug, 'en')", ("id", tenant), ("slug", slug));
            await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
            await ExecuteAsync(owner, "insert into tenant_hosts (host, tenant_id, is_primary) values (@host, @tenant, true)", ("host", host), ("tenant", tenant));
            await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key) values (@tenant, 'feature', 'demo')", ("tenant", tenant));
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertInsufficientPrivilegeAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal("42501", exception.SqlState);
    }
}
