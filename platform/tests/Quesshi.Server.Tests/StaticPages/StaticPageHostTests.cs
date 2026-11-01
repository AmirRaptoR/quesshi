using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests.StaticPages;

/// <summary>The server-rendered landing and join pages, fetched over HTTP the way a browser without scripts would.</summary>
public sealed class StaticPageHostTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new StaticPageFeature()]);
        await _host.StartAsync();
        await _host.ExecuteOwnerAsync("""
            update tenants set name = 'Test Quiz', default_language = 'en',
                branding = '{"appNames":{"fa":"آزمون"},"landingTexts":{"en":"A tenant lede."},"icon":"/media/tenant-logo.png"}'::jsonb
            where id = @t;
            insert into tenant_languages(tenant_id, code, name, english_name, direction, sort_order)
            values (@t, 'en', 'English', 'English', 'ltr', 0), (@t, 'fa', 'فارسی', 'Persian', 'rtl', 1);
            """, new { t = _host.TenantId });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task The_landing_page_for_a_signed_out_visitor_is_server_rendered_with_the_tenant_content()
    {
        var response = await GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1>Test Quiz</h1>", html);
        Assert.Contains("A tenant lede.", html);
        Assert.Contains("Never online together", html);
        Assert.Contains("href=\"/signin?next=/play\"", html);
        Assert.DoesNotContain("<script", html);
    }

    [SkippableFact]
    public async Task A_signed_in_visitor_on_the_root_gets_the_app_instead_of_the_landing_page()
    {
        var response = await Host.Player.GetAsync("/");

        // The test host has no published client, so the shell may be absent; the landing page must not be served.
        Assert.DoesNotContain("Never online together", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task The_join_page_is_a_static_preview_with_a_join_button_and_an_empty_app_container_and_has_no_lobby_content()
    {
        var response = await GetAsync("/join/ABC123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var preview = Between(html, "<div id=\"preview\">", "<div id=\"app\">");
        Assert.Contains("id=\"join-button\"", preview);
        Assert.Contains("href=\"/signin?next=%2Fjoin%2FABC123\"", preview);
        Assert.Contains("<div id=\"app\"></div>", html);
        Assert.Contains("<script src=\"_framework/blazor.webassembly.js\"></script>", html);
        Assert.DoesNotContain("ABC123</", preview);
    }

    [SkippableFact]
    public async Task The_join_page_is_identical_for_a_real_lobby_code_and_an_unknown_one()
    {
        await Host.SetCapabilityAsync("format", "async_duel", true);
        var created = await Host.Player.PostAsJsonAsync("/api/sessions", new
        {
            id = Guid.NewGuid(),
            format = "async_duel",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 4,
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var realCode = document.RootElement.GetProperty("code").GetString()!;
        var unknownCode = realCode == "ZZZZZZ" ? "YYYYYY" : "ZZZZZZ";

        var real = await (await GetAsync($"/join/{realCode}")).Content.ReadAsStringAsync();
        var unknown = await (await GetAsync($"/join/{unknownCode}")).Content.ReadAsStringAsync();

        // The code only appears in the sign-in fallback link; nothing else may differ if no lobby was looked up.
        Assert.Equal(unknown.Replace(unknownCode, "CODE"), real.Replace(realCode, "CODE"));
    }

    [SkippableFact]
    public async Task The_join_page_carries_open_graph_and_twitter_tags_for_the_tenant()
    {
        var response = await GetAsync("/join/ABC123");

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var origin = $"http://{Host.TenantOrigin.Authority}";
        foreach (var prefix in new[] { "og", "twitter" })
        {
            var attribute = prefix == "og" ? "property" : "name";
            Assert.Contains($"<meta {attribute}=\"{prefix}:title\" content=\"You've been challenged · Test Quiz\"", html);
            Assert.Contains($"<meta {attribute}=\"{prefix}:description\" content=\"A tenant lede.\"", html);
            Assert.Contains($"<meta {attribute}=\"{prefix}:image\" content=\"{origin}/media/tenant-logo.png\"", html);
        }
    }

    [SkippableFact]
    public async Task The_lang_cookie_decides_language_and_direction_when_the_tenant_offers_it()
    {
        var response = await GetAsync("/", cookie: "lang=fa", acceptLanguage: "en");

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("<html lang=\"fa\" dir=\"rtl\"", html);
        Assert.Contains("<h1>آزمون</h1>", html);
    }

    [SkippableFact]
    public async Task A_cookie_for_a_language_the_tenant_does_not_offer_is_ignored()
    {
        var response = await GetAsync("/join/ABC123", cookie: "lang=nl", acceptLanguage: "fa");

        Assert.Contains("<html lang=\"fa\" dir=\"rtl\"", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Without_a_cookie_the_browser_language_decides_when_the_tenant_offers_it()
    {
        var html = await (await GetAsync("/", acceptLanguage: "de;q=0.9,fa-IR;q=0.8")).Content.ReadAsStringAsync();

        Assert.Contains("<html lang=\"fa\" dir=\"rtl\"", html);
    }

    [SkippableFact]
    public async Task Without_a_usable_language_the_tenant_default_is_used_and_an_rtl_default_is_rtl()
    {
        var english = await (await GetAsync("/", acceptLanguage: "de")).Content.ReadAsStringAsync();
        Assert.Contains("<html lang=\"en\" dir=\"ltr\"", english);

        await Host.ExecuteOwnerAsync("update tenants set default_language = 'fa' where id = @t", new { t = Host.TenantId });
        try
        {
            var persian = await (await GetAsync("/join/ABC123", acceptLanguage: "de")).Content.ReadAsStringAsync();
            Assert.Contains("<html lang=\"fa\" dir=\"rtl\"", persian);
        }
        finally
        {
            await Host.ExecuteOwnerAsync("update tenants set default_language = 'en' where id = @t", new { t = Host.TenantId });
        }
    }

    [SkippableTheory]
    [InlineData("/")]
    [InlineData("/join/ABC123")]
    public async Task Both_pages_vary_on_cookie_and_accept_language(string path)
    {
        var response = await GetAsync(path);

        Assert.Contains("Cookie", response.Headers.Vary);
        Assert.Contains("Accept-Language", response.Headers.Vary);
    }

    [SkippableFact]
    public async Task Both_pages_are_readable_in_a_real_browser_with_javascript_disabled()
    {
        var browserTests = FindBrowserTests();
        var installed = browserTests is not null && Directory.Exists(Path.Combine(browserTests, "node_modules", "playwright"));
        // The Platform workflow installs Playwright before this runs, so a missing install there is a failure.
        Skip.If(!installed && Environment.GetEnvironmentVariable("CI") != "true", "Playwright is not installed under tests/browser.");
        Assert.True(installed, "Playwright is not installed under tests/browser.");

        var start = new ProcessStartInfo("node", Path.Combine(browserTests!, "src", "static-pages-check.mjs"))
        {
            WorkingDirectory = browserTests!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["PLATFORM_STATIC_PAGES_HOST"] = Host.TenantOrigin.Host;
        start.Environment["PLATFORM_STATIC_PAGES_PORT"] = Host.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.True(process.ExitCode == 0, $"{await output}\n{await errors}");
    }

    private static string? FindBrowserTests()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "browser", "src", "static-pages-check.mjs");
            if (File.Exists(candidate)) return Path.Combine(directory.FullName, "tests", "browser");
        }
        return null;
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(_host is null, "QUESSHI_TEST_POSTGRES is not set.");
            return _host!;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie = null, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (acceptLanguage is not null) request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        return await Host.Anonymous.SendAsync(request);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"'{start}' ... '{end}' not found.");
        return text[from..to];
    }

    private sealed class StaticPageFeature : IFeature
    {
        public string Id => "static-pages";
        public FeatureSettings Settings => FeatureSettings.Create(new StaticPageSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record StaticPageSettings;
}
