using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class CapabilityEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new CapabilityHttpFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("switch", "sessions", true);
        await SeedOtherTenantAsync(_host);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Owner_list_includes_current_tenant_capability_rows()
    {
        var capabilities = await ListAsync();

        Assert.Contains(capabilities, capability => capability.Type == "switch" && capability.Key == "sessions");
    }

    [SkippableFact]
    public async Task Owner_list_excludes_other_tenant_capability_rows()
    {
        var capabilities = await ListAsync();

        Assert.DoesNotContain(capabilities, capability => capability.Key == "guests");
    }

    [SkippableFact]
    public async Task Owner_changes_a_capability_enabled_state()
    {
        using var response = await UpdateAsync(Host.Owner, "feature", "capability_http", new { enabled = false, settings = new { } });
        response.EnsureSuccessStatusCode();
        var enabled = await Host.QueryOwnerAsync<bool>("""
            select enabled from tenant_capabilities
            where tenant_id = @tenantId and type = 'feature' and key = 'capability_http'
            """, new { tenantId = Host.TenantId });

        Assert.False(Assert.Single(enabled));
    }

    [SkippableFact]
    public async Task Owner_update_returns_the_registered_validator_messages()
    {
        using var response = await UpdateAsync(Host.Owner, "switch", "sessions", new { enabled = true, settings = new { capacity = 0 } });
        var error = await response.Content.ReadFromJsonAsync<CapabilityErrorResponse>();

        Assert.Equal((HttpStatusCode.BadRequest, "Capacity must be between 1 and 500."), (response.StatusCode, string.Join("\n", error?.Messages ?? [])));
    }

    [SkippableFact]
    public async Task Owner_update_refuses_an_entitlement_change()
    {
        using var response = await UpdateAsync(Host.Owner, "feature", "capability_http", new { enabled = true, settings = new { }, entitled = false });
        var error = await response.Content.ReadFromJsonAsync<CapabilityErrorResponse>();

        Assert.Equal((HttpStatusCode.Forbidden, "entitlement_platform_controlled"), (response.StatusCode, error?.Error));
    }

    [SkippableFact]
    public async Task Owner_update_round_trips_field_bearing_signin_settings_to_config()
    {
        const string clientId = "capability-settings.apps.googleusercontent.com";
        await Host.SetCapabilityAsync("signin", "google", true);
        using var update = await UpdateAsync(Host.Owner, "signin", "google", new { enabled = true, settings = new { clientId } });
        update.EnsureSuccessStatusCode();

        using var response = await Host.Anonymous.GetAsync("/api/config");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var signInMethods = document.RootElement.GetProperty("signInMethods").EnumerateArray();
        var google = Assert.Single(signInMethods, method => method.GetProperty("id").GetString() == "google");

        Assert.Equal(clientId, google.GetProperty("settings").GetProperty("clientId").GetString());
    }

    [SkippableFact]
    public async Task Owner_update_round_trips_bank_settings_to_the_category_allow_list()
    {
        var allowed = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into categories(tenant_id, id, slug, names, is_active, sort_order)
            values (@tenantId, @allowed, 'capability-bank-allowed', '{"en":"Allowed"}'::jsonb, true, 0),
                   (@tenantId, @excluded, 'capability-bank-excluded', '{"en":"Excluded"}'::jsonb, true, 1);
            """, new { tenantId = Host.TenantId, allowed, excluded });
        await Host.SetCapabilityAsync("source", "bank", true);
        using var update = await UpdateAsync(Host.Owner, "source", "bank", new
        {
            enabled = true,
            settings = new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [allowed] } },
        });
        update.EnsureSuccessStatusCode();

        using var response = await Host.Anonymous.GetAsync("/api/categories?family=trivia");
        response.EnsureSuccessStatusCode();
        var category = Assert.Single(await response.Content.ReadFromJsonAsync<IReadOnlyList<CategoryResponse>>() ?? []);

        Assert.Equal(allowed, category.Id);
    }

    [SkippableFact]
    public async Task Category_allow_list_reads_legacy_pascal_case_bank_settings()
    {
        var allowed = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into categories(tenant_id, id, slug, names, is_active, sort_order)
            values (@tenantId, @allowed, 'legacy-bank-allowed', '{"en":"Allowed"}'::jsonb, true, 0),
                   (@tenantId, @excluded, 'legacy-bank-excluded', '{"en":"Excluded"}'::jsonb, true, 1);
            """, new { tenantId = Host.TenantId, allowed, excluded });
        await Host.SetCapabilityAsync("source", "bank", true);
        await Host.ExecuteOwnerAsync("""
            update tenant_capabilities
            set settings = jsonb_build_object('CategoriesByFamily', jsonb_build_object('trivia', jsonb_build_array(@allowed)))
            where tenant_id = @tenantId and type = 'source' and key = 'bank';
            """, new { tenantId = Host.TenantId, allowed });

        using var response = await Host.Anonymous.GetAsync("/api/categories?family=trivia");
        response.EnsureSuccessStatusCode();
        var category = Assert.Single(await response.Content.ReadFromJsonAsync<IReadOnlyList<CategoryResponse>>() ?? []);

        Assert.Equal(allowed, category.Id);
    }

    [SkippableFact]
    public async Task Successful_capability_update_advances_config_version()
    {
        var before = await ConfigVersionAsync();
        using var response = await UpdateAsync(Host.Owner, "feature", "capability_http", new { enabled = false, settings = new { } });
        response.EnsureSuccessStatusCode();
        var after = await ConfigVersionAsync();

        Assert.Equal(before + 1, after);
    }

    [SkippableFact]
    public async Task A_config_version_failure_rolls_back_the_capability_change()
    {
        var before = await ConfigVersionAsync();
        await Host.ExecuteOwnerAsync("""
            create function reject_capability_config_version() returns trigger language plpgsql as $$
            begin
                raise exception 'reject config version';
            end;
            $$;
            create trigger reject_capability_config_version before update of config_version on tenants
            for each row execute function reject_capability_config_version();
            """, new { });
        try
        {
            using var response = await UpdateAsync(Host.Owner, "feature", "capability_http", new { enabled = false, settings = new { } });
            var enabled = await Host.QueryOwnerAsync<bool>("""
                select enabled from tenant_capabilities
                where tenant_id = @tenantId and type = 'feature' and key = 'capability_http'
                """, new { tenantId = Host.TenantId });
            var after = await ConfigVersionAsync();

            Assert.Equal((HttpStatusCode.InternalServerError, true, before), (response.StatusCode, enabled.Single(), after));
        }
        finally
        {
            await Host.ExecuteOwnerAsync("drop trigger reject_capability_config_version on tenants; drop function reject_capability_config_version()", new { });
        }
    }

    [SkippableFact]
    public async Task Successful_capability_update_sends_config_to_an_open_player_stream()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
#if QUESSHI_TEST_IDENTITY
        request.Headers.Add("X-Quesshi-Test-Player", Host.PlayerId.ToString());
#endif
        using var response = await Host.Player.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true);
        _ = await ReadFrameAsync(reader);

        using var update = await UpdateAsync(Host.Owner, "feature", "capability_http", new { enabled = false, settings = new { } });
        update.EnsureSuccessStatusCode();
        var frame = await ReadFrameAsync(reader);

        Assert.Contains("event: config\ndata: {\"version\":1}", frame);
    }

    [SkippableFact]
    public async Task Saved_sessions_capacity_rejects_an_oversized_new_lobby()
    {
        await Host.SetCapabilityAsync("format", "async_duel", true);
        using var update = await UpdateAsync(Host.Owner, "switch", "sessions", new { enabled = true, settings = new { capacity = 1 } });
        update.EnsureSuccessStatusCode();
        using var lobby = await Host.Player.PostAsJsonAsync("/api/sessions", new
        {
            id = Guid.NewGuid(),
            format = "async_duel",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 2,
        });

        Assert.Equal(HttpStatusCode.BadRequest, lobby.StatusCode);
    }

    [SkippableFact]
    public async Task Capability_update_role_matrix_admits_owner_and_denies_moderator()
    {
        await RoleMatrix.ForOwnerOnlyNoContentMutation(Host).AssertAsync(client =>
            UpdateAsync(client, "feature", "capability_http", new { enabled = false, settings = new { } }));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private async Task<IReadOnlyList<CapabilityResponse>> ListAsync()
    {
        using var response = await Host.Owner.GetAsync("/api/admin/capabilities");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<CapabilityResponse>>()
            ?? throw new InvalidOperationException("The capability response was empty.");
    }

    private async Task<long> ConfigVersionAsync()
    {
        var versions = await Host.QueryOwnerAsync<long>("select config_version from tenants where id = @tenantId", new { tenantId = Host.TenantId });
        return Assert.Single(versions);
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, string type, string key, object request) =>
        client.PutAsJsonAsync($"/api/admin/capabilities/{type}/{key}", request);

    private static async Task<string> ReadFrameAsync(StreamReader reader)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frame = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);
            if (line is null || line.Length == 0) return frame.Append('\n').ToString();
            frame.Append(line).Append('\n');
        }
    }

    private static async Task SeedOtherTenantAsync(PlatformHttpFixture host)
    {
        var otherTenant = Guid.NewGuid();
        await host.ExecuteOwnerAsync("""
            insert into tenants(id, slug, name, default_language)
            values (@otherTenant, @slug, 'Other capability tenant', 'en');
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
            values (@otherTenant, 'switch', 'guests', true, true);
            """, new { otherTenant, slug = $"other-capability-{otherTenant:N}" });
    }

    private sealed record CapabilityResponse(string Type, string Key, bool Entitled, bool Enabled);
    private sealed record CategoryResponse(Guid Id);
    private sealed record CapabilityErrorResponse(string Error, IReadOnlyList<string>? Messages);

    private sealed class CapabilityHttpFeature : IFeature
    {
        public string Id => "capability_http";
        public FeatureSettings Settings => FeatureSettings.Create(new CapabilityHttpSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record CapabilityHttpSettings;
}
