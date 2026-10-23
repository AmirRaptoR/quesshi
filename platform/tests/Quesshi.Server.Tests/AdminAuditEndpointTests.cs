using System.Net.Http.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class AdminAuditEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new AuditHttpFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Successful_owner_admin_mutation_appears_in_the_owner_audit_result()
    {
        await CreateAdminAsync(Host.Owner, "visible");

        var page = await ReadAuditAsync(Host.Owner, "?take=10");

        Assert.Contains(page.Items, entry =>
            entry.Action == "admin.account.create" &&
            entry.ActorType == "admin" &&
            entry.ActorId is not null &&
            entry.TargetType == "admin");
    }

    [SkippableFact]
    public async Task Owner_audit_result_pages_older_events_in_descending_order()
    {
        await CreateAdminAsync(Host.Owner, "first");
        await CreateAdminAsync(Host.Owner, "second");
        await CreateAdminAsync(Host.Owner, "third");

        var newest = await ReadAuditAsync(Host.Owner, "?take=1");
        var older = await ReadAuditAsync(Host.Owner, $"?take=1&before={newest.Next}");

        Assert.True(newest.Next is not null && newest.Items.Single().Id == newest.Next && older.Items.Single().Id < newest.Items.Single().Id);
    }

    [SkippableFact]
    public async Task Owner_audit_result_excludes_plausible_events_from_another_tenant()
    {
        await CreateAdminAsync(Host.Owner, "local");
        using var otherOwner = await Host.CreateOtherTenantOwnerClientAsync();
        await CreateAdminAsync(otherOwner, "foreign");
        var foreign = await ReadAuditAsync(otherOwner, "?take=10");
        var local = await ReadAuditAsync(Host.Owner, "?take=10");
        var foreignEventId = foreign.Items.Single(entry => entry.Action == "admin.account.create").Id;

        Assert.DoesNotContain(local.Items, entry => entry.Id == foreignEventId);
    }

    [SkippableFact]
    public async Task Owner_audit_route_enforces_the_administration_role_matrix()
    {
        await RoleMatrix.ForOwnerOnlyRead(Host).AssertAsync(client => client.GetAsync("/api/admin/audit"));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private static async Task CreateAdminAsync(HttpClient client, string marker)
    {
        var unique = Guid.NewGuid().ToString("N")[..12];
        using var response = await client.PostAsJsonAsync("/api/admin/accounts", new
        {
            username = $"audit-{marker}-{unique}",
            email = $"audit-{marker}-{unique}@example.test",
            password = "Audit password 2026!",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<AuditPage> ReadAuditAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"/api/admin/audit{query}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuditPage>()
            ?? throw new InvalidOperationException("The audit endpoint returned no page.");
    }

    private sealed record AuditPage(IReadOnlyList<AuditEntry> Items, long? Next);

    private sealed record AuditEntry(long Id, string ActorType, Guid? ActorId, string Action, string TargetType, Guid? TargetId, DateTimeOffset At);

    private sealed class AuditHttpFeature : IFeature
    {
        public string Id => "audit_http";
        public FeatureSettings Settings => FeatureSettings.Create(new AuditHttpSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record AuditHttpSettings;
}
