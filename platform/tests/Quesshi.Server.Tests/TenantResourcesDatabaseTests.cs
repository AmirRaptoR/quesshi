using Dapper;
using Microsoft.AspNetCore.Builder;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Testing;
using System.Net;
using System.Text.Json;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class TenantResourcesDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Resources_are_host_scoped_and_revalidate_rendered_content()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));

        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();
        await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
        {
            await owner.OpenAsync();
            await SeedAsync(owner, firstTenant, "first", "First tenant", "en", "first.example.test", """
                {"palette":{"firouzeh":"#123456","shab":"#101112"},"appNames":{"en":"First app"},"icon":"/tenant-first.png"}
                """);
            await SeedAsync(owner, secondTenant, "second", "Second tenant", "ar", "second.example.test", """
                {"palette":{"zaferan":"#abcdef"}}
                """);
            await owner.ExecuteAsync("""
                insert into tenant_languages(tenant_id, code, name, english_name, direction, sort_order)
                values (@firstTenant, 'en', 'English', 'English', 'ltr', 0),
                       (@firstTenant, 'ar', 'فارسی', 'Persian', 'rtl', 1),
                       (@secondTenant, 'ar', 'العربية', 'Arabic', 'rtl', 0)
                """, new { firstTenant, secondTenant });
            await owner.ExecuteAsync("""
                insert into tenant_capabilities(tenant_id, type, key, entitled, enabled)
                values (@firstTenant, 'switch', 'guests', true, true),
                       (@firstTenant, 'switch', 'custom_questions', true, false),
                       (@firstTenant, 'feature', 'unregistered', true, true),
                       (@secondTenant, 'switch', 'custom_questions', true, true)
                """, new { firstTenant, secondTenant });
        }

        await using var sources = CreateSources();
        await using var app = Program.CreateApplication(sources, 11129, 30129);
        app.Urls.Add("http://127.0.0.1:50129");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50129") };

            var firstConfig = await SendAsync(client, "/api/config", "first.example.test");
            Assert.Equal(HttpStatusCode.OK, firstConfig.StatusCode);
            Assert.Equal("no-cache", firstConfig.Headers.CacheControl?.ToString());
            var firstConfigEtag = Assert.IsType<string>(firstConfig.Headers.ETag?.Tag);
            using (var config = JsonDocument.Parse(await firstConfig.Content.ReadAsStringAsync()))
            {
                var root = config.RootElement;
                Assert.Equal("First tenant", root.GetProperty("name").GetString());
                Assert.Equal("en", root.GetProperty("defaultLanguage").GetString());
                Assert.Equal(1, root.GetProperty("configVersion").GetInt64());
                var languages = root.GetProperty("languages").EnumerateArray().ToArray();
                Assert.Equal(["en", "ar"], languages.Select(language => language.GetProperty("code").GetString()!).ToArray());
                Assert.Equal("English", languages[0].GetProperty("displayName").GetString());
                Assert.Equal("ltr", languages[0].GetProperty("direction").GetString());
                Assert.Equal("First app", languages[0].GetProperty("appName").GetString());
                Assert.Equal("rtl", languages[1].GetProperty("direction").GetString());
                var capabilities = root.GetProperty("capabilities");
                Assert.Equal(["guests"], capabilities.GetProperty("switch").EnumerateArray().Select(value => value.GetString()!).ToArray());
                Assert.Empty(capabilities.GetProperty("feature").EnumerateArray());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("serverBuildId").GetString()));
            }

            var revalidated = await SendAsync(client, "/api/config", "first.example.test", firstConfigEtag);
            Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
            Assert.Equal(0L, revalidated.Content.Headers.ContentLength);

            var secondConfig = await SendAsync(client, "/api/config", "second.example.test");
            using (var config = JsonDocument.Parse(await secondConfig.Content.ReadAsStringAsync()))
            {
                Assert.Equal("Second tenant", config.RootElement.GetProperty("name").GetString());
                Assert.Equal(["custom_questions"], config.RootElement.GetProperty("capabilities").GetProperty("switch").EnumerateArray().Select(value => value.GetString()!).ToArray());
            }
            var secondTheme = await SendAsync(client, "/theme.css", "second.example.test");
            Assert.Contains("--zaferan: #abcdef;", await secondTheme.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var secondManifest = await SendAsync(client, "/manifest.webmanifest", "second.example.test");
            using (var document = JsonDocument.Parse(await secondManifest.Content.ReadAsStringAsync()))
            {
                Assert.Equal("Second tenant", document.RootElement.GetProperty("name").GetString());
                Assert.Equal("#090E22", document.RootElement.GetProperty("theme_color").GetString());
                Assert.Equal("icon-192.png", document.RootElement.GetProperty("icons")[0].GetProperty("src").GetString());
            }

            var theme = await SendAsync(client, "/theme.css", "first.example.test");
            Assert.Equal(HttpStatusCode.OK, theme.StatusCode);
            var themeEtag = Assert.IsType<string>(theme.Headers.ETag?.Tag);
            Assert.Equal("no-cache", theme.Headers.CacheControl?.ToString());
            var themeContent = await theme.Content.ReadAsStringAsync();
            Assert.Contains("--firouzeh: #123456;", themeContent, StringComparison.Ordinal);
            Assert.Contains("--shab: #101112;", themeContent, StringComparison.Ordinal);
            Assert.Contains("--zaferan: #E8A83A;", themeContent, StringComparison.Ordinal);

            var manifest = await SendAsync(client, "/manifest.webmanifest", "first.example.test");
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            var manifestEtag = Assert.IsType<string>(manifest.Headers.ETag?.Tag);
            Assert.Equal("no-cache", manifest.Headers.CacheControl?.ToString());
            using (var document = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync()))
            {
                Assert.Equal("First tenant", document.RootElement.GetProperty("name").GetString());
                Assert.Equal("#101112", document.RootElement.GetProperty("theme_color").GetString());
                Assert.Equal("/tenant-first.png", document.RootElement.GetProperty("icons")[0].GetProperty("src").GetString());
            }

            var unknown = await SendAsync(client, "/api/config", "unknown.example.test");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            Assert.Equal(HttpStatusCode.NotModified, (await SendAsync(client, "/theme.css", "first.example.test", themeEtag)).StatusCode);
            Assert.Equal(HttpStatusCode.NotModified, (await SendAsync(client, "/manifest.webmanifest", "first.example.test", manifestEtag)).StatusCode);

            await using (var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner")))
            {
                await owner.OpenAsync();
                await owner.ExecuteAsync("update tenants set branding = cast(@branding as jsonb) where id = @firstTenant", new
                {
                    firstTenant,
                    branding = """{"palette":{"firouzeh":"#ABCDEF;body{display:none}","shab":"#223344"}}"""
                });
                await owner.ExecuteAsync("update tenant_languages set name = 'English changed' where tenant_id = @firstTenant and code = 'en'", new { firstTenant });
                await owner.ExecuteAsync("update tenant_capabilities set enabled = true where tenant_id = @firstTenant and type = 'switch' and key = 'custom_questions'", new { firstTenant });
            }

            var updatedTheme = await SendAsync(client, "/theme.css", "first.example.test", themeEtag);
            Assert.Equal(HttpStatusCode.OK, updatedTheme.StatusCode);
            Assert.NotEqual(themeEtag, Assert.IsType<string>(updatedTheme.Headers.ETag?.Tag));
            var updatedThemeContent = await updatedTheme.Content.ReadAsStringAsync();
            Assert.Contains("--firouzeh: #2FBFB0;", updatedThemeContent, StringComparison.Ordinal);
            Assert.Contains("--shab: #223344;", updatedThemeContent, StringComparison.Ordinal);
            Assert.DoesNotContain("body{display:none}", updatedThemeContent, StringComparison.Ordinal);

            var updatedManifest = await SendAsync(client, "/manifest.webmanifest", "first.example.test", manifestEtag);
            Assert.Equal(HttpStatusCode.OK, updatedManifest.StatusCode);
            Assert.NotEqual(manifestEtag, Assert.IsType<string>(updatedManifest.Headers.ETag?.Tag));
            using (var document = JsonDocument.Parse(await updatedManifest.Content.ReadAsStringAsync()))
            {
                Assert.Equal("#223344", document.RootElement.GetProperty("theme_color").GetString());
                Assert.Equal("icon-192.png", document.RootElement.GetProperty("icons")[0].GetProperty("src").GetString());
            }

            var updatedConfig = await SendAsync(client, "/api/config", "first.example.test", firstConfigEtag);
            Assert.Equal(HttpStatusCode.OK, updatedConfig.StatusCode);
            Assert.NotEqual(firstConfigEtag, Assert.IsType<string>(updatedConfig.Headers.ETag?.Tag));
            using var updatedConfigDocument = JsonDocument.Parse(await updatedConfig.Content.ReadAsStringAsync());
            Assert.Equal("English changed", updatedConfigDocument.RootElement.GetProperty("languages")[0].GetProperty("displayName").GetString());
            Assert.Equal(["custom_questions", "guests"], updatedConfigDocument.RootElement.GetProperty("capabilities").GetProperty("switch").EnumerateArray().Select(value => value.GetString()!).Order().ToArray());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task SeedAsync(NpgsqlConnection connection, Guid id, string slug, string name, string defaultLanguage, string host, string branding)
    {
        await connection.ExecuteAsync("""
            insert into tenants(id, slug, name, default_language, branding, config_version)
            values (@id, @slug, @name, @defaultLanguage, cast(@branding as jsonb), 1)
            """, new { id, slug, name, defaultLanguage, branding });
        await connection.ExecuteAsync("insert into tenant_hosts(tenant_id, host) values (@id, @host)", new { id, host });
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string host, string? etag = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return await client.SendAsync(request);
    }

    private DatabaseSources CreateSources() => new(
        fixture.ConnectionStringForRole("quesshi_app"),
        fixture.ConnectionStringForRole("quesshi_system"));
}
