using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Tenancy;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class PostgresTenantsTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("1e7d9415-30ac-4cce-9e3f-5cb949cd2d6e");
    private static readonly Guid TenantB = Guid.Parse("a487fa84-7cd6-4f4e-8d8b-624ce7536844");

    [SkippableFact]
    public async Task Lookup_uses_system_database_and_refreshes_cached_hits_and_misses()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await SeedAsync(owner);

        var clock = new MutableTimeProvider();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var tenants = new PostgresTenants(sources, clock);

        Assert.Equal(TenantA, (await tenants.ResolveAsync("a.example.test"))!.Id);
        Assert.Null(await tenants.ResolveAsync("new.example.test"));

        await ExecuteAsync(owner, "update tenant_hosts set tenant_id = @tenant, is_primary = false where host = 'a.example.test'", ("tenant", TenantB));
        await ExecuteAsync(owner, "insert into tenant_hosts (host, tenant_id) values ('new.example.test', @tenant)", ("tenant", TenantA));

        Assert.Equal(TenantA, (await tenants.ResolveAsync("a.example.test"))!.Id);
        Assert.Null(await tenants.ResolveAsync("new.example.test"));

        clock.Advance(PostgresTenants.CacheLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(TenantB, (await tenants.ResolveAsync("a.example.test"))!.Id);
        Assert.Equal(TenantA, (await tenants.ResolveAsync("new.example.test"))!.Id);
    }

    private static async Task SeedAsync(NpgsqlConnection owner)
    {
        await ExecuteAsync(owner, "delete from tenant_capabilities; delete from tenant_hosts; delete from tenant_languages; delete from tenants");
        foreach (var (tenant, slug, host) in new[] { (TenantA, "tenant-a", "a.example.test"), (TenantB, "tenant-b", "b.example.test") })
        {
            await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@id, @slug, @slug, 'en')", ("id", tenant), ("slug", slug));
            await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
            await ExecuteAsync(owner, "insert into tenant_hosts (host, tenant_id, is_primary) values (@host, @tenant, true)", ("host", host), ("tenant", tenant));
            await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key) values (@tenant, 'feature', 'demo')", ("tenant", tenant));
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
