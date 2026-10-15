using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Database;
using Quesshi.Server;
using Quesshi.Server.Commands;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class TenantAddDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Add_creates_a_complete_tenant_with_canonical_primary_host_and_capabilities()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from sessions; delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        await using var sources = Sources();

        var tenant = await Program.AddTenantAsync([
            "--slug", "demo",
            "--name", "Demo tenant",
            "--host", "EXAMPLE.test.",
            "--default-language", "en",
            "--language", "en:English:English:ltr",
            "--language", "fa:فارسی:Persian:rtl"], sources);

        Assert.Equal("demo", await ScalarAsync<string>(owner, "select slug from tenants where id = @tenant", ("tenant", tenant)));
        Assert.Equal("example.test", await ScalarAsync<string>(owner, "select host from tenant_hosts where tenant_id = @tenant and is_primary", ("tenant", tenant)));
        Assert.Equal(2L, await ScalarAsync<long>(owner, "select count(*) from tenant_languages where tenant_id = @tenant", ("tenant", tenant)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from tenant_hosts where tenant_id = @tenant and is_primary", ("tenant", tenant)));
        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from tenant_capabilities where tenant_id = @tenant and not entitled and not enabled", ("tenant", tenant)));
    }

    [SkippableFact]
    public async Task Invalid_or_conflicting_adds_leave_no_partial_rows()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from sessions; delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        await using var sources = Sources();

        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "invalid", "--name", "Invalid", "--host", "invalid.example.test", "--default-language", "en", "--language", "en:English:English:sideways"], sources));
        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "missing-default", "--name", "Missing default", "--host", "missing-default.example.test", "--default-language", "fa", "--language", "en:English:English:ltr"], sources));
        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "duplicate-language", "--name", "Duplicate language", "--host", "duplicate-language.example.test", "--default-language", "en",
            "--language", "en:English:English:ltr", "--language", "en:English:English:ltr"], sources));
        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "missing-host", "--name", "Missing host", "--default-language", "en", "--language", "en:English:English:ltr"], sources));
        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "missing-language", "--name", "Missing language", "--host", "missing-language.example.test", "--default-language", "en"], sources));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from tenants"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from tenant_hosts"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from tenant_languages"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from tenant_capabilities"));

        await Program.AddTenantAsync([
            "--slug", "existing", "--name", "Existing", "--host", "existing.example.test", "--default-language", "en", "--language", "en:English:English:ltr"], sources);
        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "existing", "--name", "Replacement", "--host", "replacement.example.test", "--default-language", "en", "--language", "en:English:English:ltr"], sources));
        await Assert.ThrowsAsync<CommandException>(() => Program.AddTenantAsync([
            "--slug", "rolled-back", "--name", "Rolled back", "--host", "existing.example.test", "--default-language", "en", "--language", "en:English:English:ltr"], sources));

        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from tenants"));
        Assert.Equal("Existing", await ScalarAsync<string>(owner, "select name from tenants where slug = 'existing'"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from tenants where slug = 'rolled-back'"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from tenant_hosts"));
        Assert.Equal("existing.example.test", await ScalarAsync<string>(owner, "select host from tenant_hosts"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from tenant_languages"));
        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from tenant_capabilities"));
        Assert.Equal(3L, await ScalarAsync<long>(owner, "select count(*) from tenant_capabilities where not entitled and not enabled"));
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
