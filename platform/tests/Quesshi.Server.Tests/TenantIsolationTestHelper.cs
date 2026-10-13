using Npgsql;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

internal static class TenantIsolationTestHelper
{
    public static async Task AssertTenantTableAsync(PostgresFixture fixture, string table, Guid tenantA, Guid tenantB)
    {
        await using var owner = await OpenAsync(fixture, "quesshi_owner");
        await ExecuteAsync(owner, $"drop table if exists {table} cascade");
        await ExecuteAsync(owner, $"create table {table} (tenant_id uuid not null, id uuid not null, value text not null, primary key (tenant_id, id))");
        await ExecuteAsync(owner, $"select quesshi_tenant_table('{table}', 'test')");
        await ExecuteAsync(owner, $"insert into {table} (tenant_id, id, value) values (@tenant, @id, @value)", ("tenant", tenantA), ("id", tenantA), ("value", "a"));
        await ExecuteAsync(owner, $"insert into {table} (tenant_id, id, value) values (@tenant, @id, @value)", ("tenant", tenantB), ("id", tenantB), ("value", "b"));

        await using var app = await OpenAsync(fixture, "quesshi_app");
        await ExecuteAsync(app, "select set_config('app.tenant_id', @tenant::text, false)", ("tenant", tenantA));
        Assert.Equal(1L, await ScalarAsync<long>(app, $"select count(*) from {table}"));
        Assert.Equal("a", await ScalarAsync<string>(app, $"select value from {table} where id = @id", ("id", tenantA)));
        Assert.Equal(0L, await ExecuteAsync(app, $"update {table} set value = 'changed' where tenant_id = @tenant", ("tenant", tenantB)));
        Assert.Equal(0L, await ExecuteAsync(app, $"delete from {table} where tenant_id = @tenant", ("tenant", tenantB)));
        var crossTenantInsert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, $"insert into {table} (tenant_id, id, value) values (@tenant, @id, 'forbidden')", ("tenant", tenantB), ("id", Guid.NewGuid())));
        Assert.Equal("42501", crossTenantInsert.SqlState);
        await ExecuteAsync(app, $"insert into {table} (tenant_id, id, value) values (@tenant, @id, 'allowed')", ("tenant", tenantA), ("id", Guid.NewGuid()));

        await using var unbound = await OpenAsync(fixture, "quesshi_app");
        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<long>(unbound, $"select count(*) from {table}"));

        await using var system = await OpenAsync(fixture, "quesshi_system");
        var systemRead = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<long>(system, $"select count(*) from {table}"));
        Assert.Equal("42501", systemRead.SqlState);
        var systemWrite = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(system, $"delete from {table}"));
        Assert.Equal("42501", systemWrite.SqlState);
    }

    public static async Task AssertTenantCatalogueAsync(NpgsqlConnection owner, IReadOnlySet<string> globalTables)
    {
        const string sql = """
            select c.relname
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            join pg_attribute a on a.attrelid = c.oid and a.attname = 'tenant_id' and not a.attisdropped
            where n.nspname = 'public' and c.relkind = 'r'
            order by c.relname
            """;
        await using var command = new NpgsqlCommand(sql, owner);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        await reader.CloseAsync();

        foreach (var table in tables)
        {
            if (globalTables.Contains(table)) continue;
            var primaryKeyStartsWithTenantId = await ScalarAsync<bool>(owner, """
                select exists (
                  select 1 from pg_index i
                  join pg_attribute a on a.attrelid = i.indrelid and a.attnum = i.indkey[0]
                  where i.indrelid = @table::regclass and i.indisprimary and a.attname = 'tenant_id')
                """, ("table", table));
            Assert.True(primaryKeyStartsWithTenantId, $"Tenant-owned table {table} must have a primary key starting with tenant_id.");
            Assert.True(
                await ScalarAsync<long>(owner, "select count(*) from pg_policies where schemaname = 'public' and tablename = @table and policyname = 'tenant_isolation'", ("table", table)) == 1,
                $"Tenant-owned table {table} must have the tenant_isolation policy.");
            Assert.StartsWith("owner:", await ScalarAsync<string>(owner, "select obj_description(@table::regclass)", ("table", table)));
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(PostgresFixture fixture, string role)
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
}
