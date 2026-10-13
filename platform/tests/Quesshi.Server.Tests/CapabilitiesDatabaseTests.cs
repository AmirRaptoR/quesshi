using Npgsql;
using Quesshi.Application;
using Quesshi.Infrastructure.Capabilities;
using Quesshi.Infrastructure.Database;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class CapabilitiesDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("bf89df6d-5719-4e6d-8c21-0bd7dcb90102");
    private static readonly Guid TenantB = Guid.Parse("c0f4a3e5-0b63-48e7-8914-1f83b4d7cb03");

    [SkippableFact]
    public async Task Settings_updates_are_validated_tenant_scoped_and_versioned()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        await SeedAsync(owner, TenantA, "tenant-a");
        await SeedAsync(owner, TenantB, "tenant-b");

        var registry = new CapabilityRegistry();
        registry.Register(CapabilityType.Feature, "demo", new DemoSettings("default"), Validate);
        await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var capabilities = new TenantCapabilities(registry, new TenantDb(sources));

        Assert.True(await capabilities.IsOnAsync(TenantA, CapabilityType.Feature, "demo"));
        Assert.False(await capabilities.IsOnAsync(TenantA, CapabilityType.Feature, "unknown"));
        await capabilities.SetAsync(TenantA, CapabilityType.Feature, "demo", false, new DemoSettings("changed"));

        Assert.False(await capabilities.IsOnAsync(TenantA, CapabilityType.Feature, "demo"));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select config_version from tenants where id = @tenant", ("tenant", TenantA)));
        Assert.True(await ScalarAsync<bool>(owner, "select entitled and not enabled from tenant_capabilities where tenant_id = @tenant and type = 'feature' and key = 'demo'", ("tenant", TenantA)));
        Assert.Equal("changed", await ScalarAsync<string>(owner, "select settings ->> 'Label' from tenant_capabilities where tenant_id = @tenant", ("tenant", TenantA)));
        Assert.Equal(new DemoSettings("changed"), await capabilities.GetSettingsAsync<DemoSettings>(TenantA, CapabilityType.Feature, "demo"));

        var exception = await Assert.ThrowsAsync<CapabilityValidationException>(() =>
            capabilities.SetAsync(TenantA, CapabilityType.Feature, "demo", true, new DemoSettings(string.Empty)));
        Assert.Equal(["A label is required."], exception.Messages);
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select config_version from tenants where id = @tenant", ("tenant", TenantA)));
        Assert.Equal("changed", await ScalarAsync<string>(owner, "select settings ->> 'Label' from tenant_capabilities where tenant_id = @tenant", ("tenant", TenantA)));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select config_version from tenants where id = @tenant", ("tenant", TenantB)));
        Assert.True(await ScalarAsync<bool>(owner, "select entitled and enabled from tenant_capabilities where tenant_id = @tenant and type = 'feature' and key = 'demo'", ("tenant", TenantB)));
    }

    private static IReadOnlyList<string> Validate(DemoSettings settings) =>
        settings.Label.Length == 0 ? ["A label is required."] : [];

    private static async Task SeedAsync(NpgsqlConnection owner, Guid tenant, string slug)
    {
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@tenant, @slug, @slug, 'en')", ("tenant", tenant), ("slug", slug));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
        await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled) values (@tenant, 'feature', 'demo', true, true)", ("tenant", tenant));
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

    private sealed record DemoSettings(string Label);
}
