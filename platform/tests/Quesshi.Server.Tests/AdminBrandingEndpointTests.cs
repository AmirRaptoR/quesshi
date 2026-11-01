using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class AdminBrandingEndpointTests : IAsyncLifetime
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

        host = new PlatformHttpFixture([new BrandingHttpFeature()]);
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
    public async Task Owner_saves_a_theme_colour_that_persists_and_bumps_the_config_version()
    {
        var before = await ConfigVersionAsync();

        using var response = await Host.Owner.PutAsJsonAsync("/api/admin/branding", new { palette = new { firouzeh = "#112233" } });

        var stored = (await Host.QueryOwnerAsync<string>(
            "select branding #>> '{palette,firouzeh}' from tenants where id = @tenantId",
            new { tenantId = Host.TenantId })).Single();
        Assert.Equal((HttpStatusCode.OK, "#112233", before + 1), (response.StatusCode, stored, await ConfigVersionAsync()));
    }

    [SkippableFact]
    public async Task Saved_theme_colour_appears_in_the_tenant_theme_stylesheet()
    {
        await SaveAsync(new { palette = new { firouzeh = "#112233" } });

        var css = await Host.Anonymous.GetStringAsync("/theme.css");

        Assert.Contains("--firouzeh: #112233;", css, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Invalid_theme_colour_is_rejected_without_changing_branding()
    {
        var before = await ConfigVersionAsync();

        using var response = await Host.Owner.PutAsJsonAsync("/api/admin/branding", new { palette = new { firouzeh = "red; } body { display: none" } });
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_branding", before), (response.StatusCode, error?.Error, await ConfigVersionAsync()));
    }

    [SkippableFact]
    public async Task Owner_selects_logo_media_that_the_branding_read_returns()
    {
        var logo = await AddImageAsync("logo-hash", "image/png");

        await SaveAsync(new { logoMediaId = logo });
        var branding = await Host.Owner.GetFromJsonAsync<JsonElement>("/api/admin/branding");

        Assert.Equal((logo.ToString(), "/media/logo-hash"), (branding.GetProperty("logoMediaId").GetString(), branding.GetProperty("logoUrl").GetString()));
    }

    [SkippableFact]
    public async Task Owner_selects_icon_media_that_the_manifest_serves()
    {
        var icon = await AddImageAsync("icon-hash", "image/webp");

        await SaveAsync(new { iconMediaId = icon });
        var manifest = await Host.Anonymous.GetFromJsonAsync<JsonElement>("/manifest.webmanifest");
        var served = manifest.GetProperty("icons")[0];

        Assert.Equal(("/media/icon-hash", "image/webp"), (served.GetProperty("src").GetString(), served.GetProperty("type").GetString()));
    }

    [SkippableFact]
    public async Task Media_that_is_not_an_image_of_the_tenant_is_rejected()
    {
        using var unknown = await Host.Owner.PutAsJsonAsync("/api/admin/branding", new { logoMediaId = Guid.NewGuid() });
        var audio = await AddMediaAsync("clip-hash", "audio", "audio/mpeg");
        using var notImage = await Host.Owner.PutAsJsonAsync("/api/admin/branding", new { iconMediaId = audio });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (unknown.StatusCode, notImage.StatusCode));
    }

    [SkippableFact]
    public async Task Owner_saves_an_app_name_for_a_tenant_language()
    {
        await SaveAsync(new { appNames = new { en = "Trivia Night" } });

        var config = await Host.Anonymous.GetFromJsonAsync<JsonElement>("/api/config");
        var language = config.GetProperty("languages").EnumerateArray().Single(item => item.GetProperty("code").GetString() == "en");

        Assert.Equal("Trivia Night", language.GetProperty("appName").GetString());
    }

    [SkippableFact]
    public async Task Owner_saves_landing_text_for_a_tenant_language()
    {
        await SaveAsync(new { landingTexts = new { en = "Welcome to the quiz." } });

        var config = await Host.Anonymous.GetFromJsonAsync<JsonElement>("/api/config");
        var language = config.GetProperty("languages").EnumerateArray().Single(item => item.GetProperty("code").GetString() == "en");

        Assert.Equal("Welcome to the quiz.", language.GetProperty("landingText").GetString());
    }

    [SkippableFact]
    public async Task Text_for_a_language_the_tenant_does_not_offer_is_rejected()
    {
        using var response = await Host.Owner.PutAsJsonAsync("/api/admin/branding", new { appNames = new { xx = "Nope" } });
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_branding_language"), (response.StatusCode, error?.Error));
    }

    [SkippableFact]
    public async Task Branding_routes_admit_an_owner_and_deny_a_moderator()
    {
        await RoleMatrix.ForOwnerOnlyRead(Host).AssertAsync(
            client => client.GetAsync("/api/admin/branding"));
        await RoleMatrix.ForOwnerOnlyMutation(Host).AssertAsync(
            client => client.PutAsJsonAsync("/api/admin/branding", new { palette = new { firouzeh = "#112233" } }));
    }

    private async Task SaveAsync(object body)
    {
        using var response = await Host.Owner.PutAsJsonAsync("/api/admin/branding", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<long> ConfigVersionAsync() => (await Host.QueryOwnerAsync<long>(
        "select config_version from tenants where id = @tenantId",
        new { tenantId = Host.TenantId })).Single();

    private Task<Guid> AddImageAsync(string hash, string mime) => AddMediaAsync(hash, "image", mime);

    private async Task<Guid> AddMediaAsync(string hash, string kind, string mime)
    {
        var id = Guid.NewGuid();
        await Host.ExecuteOwnerAsync(
            """
            insert into media_assets (tenant_id, id, kind, storage_key, mime, bytes)
            values (@tenantId, @id, @kind, @hash, @mime, 10)
            """,
            new { tenantId = Host.TenantId, id, kind, hash, mime });
        return id;
    }

    private sealed class BrandingHttpFeature : IFeature
    {
        public string Id => "branding_http";

        public FeatureSettings Settings => FeatureSettings.Create(new BrandingHttpSettings(), _ => []);

        public void Configure(IFeatureBuilder feature)
        {
        }
    }

    private sealed record BrandingHttpSettings;

    private sealed record ErrorDocument(string Error);
}
