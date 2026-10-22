using System.Net;
using System.Net.Http.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class AdminLanguageEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.Ordinal));

    private PlatformHttpFixture? host;

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.IfNot(IsPostgresConfigured, "PostgreSQL is not configured.");
            return host ?? throw new InvalidOperationException("The test host has not started.");
        }
    }

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured)
        {
            return;
        }

        host = new PlatformHttpFixture([new LanguageHttpFeature()]);
        await host.StartAsync();
        await host.ExecuteOwnerAsync(
            """
            insert into tenant_languages (tenant_id, code, name, english_name, direction, sort_order)
            values (@tenantId, 'en', 'English', 'English', 'ltr', 0)
            """,
            new { tenantId = host.TenantId });
    }

    public async Task DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Owner_adds_a_language_that_persists_in_the_tenant()
    {
        using var response = await Host.Owner.PostAsJsonAsync(
            "/api/admin/languages",
            new { code = "fa", name = "فارسی", englishName = "Persian", direction = "rtl" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var language = Assert.Single(await Host.QueryOwnerAsync<PersistedLanguage>(
            """
            select code as Code, name as Name, english_name as EnglishName, direction as Direction
            from tenant_languages
            where tenant_id = @tenantId and code = 'fa'
            """,
            new { tenantId = Host.TenantId }));

        Assert.Equal(("fa", "فارسی", "Persian", "rtl"), (language.Code, language.Name, language.EnglishName, language.Direction));
    }

    [SkippableFact]
    public async Task Owner_editing_a_local_name_updates_the_configuration_projection()
    {
        await AddPersianAsync();

        using var update = await Host.Owner.PutAsJsonAsync(
            "/api/admin/languages/fa",
            new { name = "فارسی ایران", englishName = "Persian", direction = "rtl" });
        using var config = await Host.Anonymous.GetAsync("/api/config");

        var document = await config.Content.ReadFromJsonAsync<ConfigurationDocument>();
        var language = Assert.Single(document?.Languages.Where(item => item.Code == "fa") ?? []);

        Assert.Equal((HttpStatusCode.NoContent, "فارسی ایران"), (update.StatusCode, language.DisplayName));
    }

    [SkippableFact]
    public async Task Owner_editing_an_English_name_persists_the_generation_facing_value()
    {
        await AddPersianAsync();

        using var response = await Host.Owner.PutAsJsonAsync(
            "/api/admin/languages/fa",
            new { name = "فارسی", englishName = "Farsi", direction = "rtl" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var language = Assert.Single(await Host.QueryOwnerAsync<PersistedLanguage>(
            """
            select code as Code, name as Name, english_name as EnglishName, direction as Direction
            from tenant_languages
            where tenant_id = @tenantId and code = 'fa'
            """,
            new { tenantId = Host.TenantId }));

        Assert.Equal("Farsi", language.EnglishName);
    }

    [SkippableFact]
    public async Task Owner_editing_direction_updates_the_configuration_projection()
    {
        await AddPersianAsync();

        using var update = await Host.Owner.PutAsJsonAsync(
            "/api/admin/languages/fa",
            new { name = "فارسی", englishName = "Persian", direction = "ltr" });
        using var config = await Host.Anonymous.GetAsync("/api/config");

        var document = await config.Content.ReadFromJsonAsync<ConfigurationDocument>();
        var language = Assert.Single(document?.Languages.Where(item => item.Code == "fa") ?? []);

        Assert.Equal((HttpStatusCode.NoContent, "ltr"), (update.StatusCode, language.Direction));
    }

    [SkippableFact]
    public async Task Owner_can_set_an_offered_language_as_default()
    {
        await AddPersianAsync();

        using var response = await Host.Owner.PutAsJsonAsync("/api/admin/languages/default", new { code = "fa" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var defaultLanguage = (await Host.QueryOwnerAsync<string>(
            "select default_language from tenants where id = @tenantId",
            new { tenantId = Host.TenantId })).SingleOrDefault();

        Assert.Equal("fa", defaultLanguage);
    }

    [SkippableFact]
    public async Task Owner_cannot_remove_the_default_language()
    {
        using var response = await Host.Owner.DeleteAsync("/api/admin/languages/en");
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();
        var remaining = (await Host.QueryOwnerAsync<string>(
            "select code from tenant_languages where tenant_id = @tenantId and code = 'en'",
            new { tenantId = Host.TenantId })).SingleOrDefault();

        Assert.Equal(
            (HttpStatusCode.Conflict, "default_language_cannot_be_removed", "en"),
            (response.StatusCode, error?.Error, remaining));
    }

    [SkippableFact]
    public async Task Language_routes_admit_an_owner_and_deny_a_moderator()
    {
        await RoleMatrix.ForOwnerOnlyMutation(Host).AssertAsync(
            client => client.GetAsync("/api/admin/languages"));
    }

    private async Task AddPersianAsync()
    {
        using var response = await Host.Owner.PostAsJsonAsync(
            "/api/admin/languages",
            new { code = "fa", name = "فارسی", englishName = "Persian", direction = "rtl" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed class LanguageHttpFeature : IFeature
    {
        public string Id => "language_http";

        public FeatureSettings Settings => FeatureSettings.Create(new LanguageHttpSettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
        }
    }

    private sealed record LanguageHttpSettings;

    private sealed record PersistedLanguage(string Code, string Name, string EnglishName, string Direction);

    private sealed record ErrorDocument(string Error);

    private sealed record ConfigurationDocument(IReadOnlyList<ConfigurationLanguage> Languages);

    private sealed record ConfigurationLanguage(string Code, string DisplayName, string Direction);
}
