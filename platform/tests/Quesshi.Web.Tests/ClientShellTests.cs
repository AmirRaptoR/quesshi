using System.Net;
using System.Text;
using Microsoft.JSInterop;
using Quesshi.Web.Models;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class ClientShellTests
{
    [Fact]
    public async Task Config_request_sends_build_header_and_outdated_build_reloads_once()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
            new HttpResponseMessage(HttpStatusCode.UpgradeRequired),
            new HttpResponseMessage(HttpStatusCode.UpgradeRequired));
        var reloader = new RecordingReloader();
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, reloader);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/config"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/again"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/again"));

        Assert.All(handler.Requests, request => Assert.Equal(ClientHttp.BuildId, request.Headers.GetValues("X-Client-Build").Single()));
        Assert.Equal([ClientHttp.BuildId], reloader.Builds);
    }

    [Fact]
    public async Task Newer_config_refreshes_and_replaces_the_theme_once()
    {
        var handler = new RecordingHandler(
            Config("Northwind", 3),
            Config("Contoso", 4));
        var js = new RecordingJs();
        var config = new AppConfig(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new RecordingReloader()), js);

        await config.LoadAsync();
        await config.ConfigChanged(3);
        await config.ConfigChanged(4);
        await config.ConfigChanged(4);

        Assert.Equal("Contoso", config.Current.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(["quesshi.replaceTheme"], js.Calls);
    }

    [Fact]
    public void Language_choice_prefers_valid_saved_then_browser_then_default()
    {
        var config = new TenantConfig("Tenant", [
            new TenantLanguage("en", "English", "ltr", null),
            new TenantLanguage("fa", "فارسی", "rtl", null)], "fa", new Dictionary<string, string[]>(), 1, "server");

        Assert.Equal("en", LanguageSelection.Choose(config, "en", ["fa"]));
        Assert.Equal("fa", LanguageSelection.Choose(config, "removed", ["fa-IR"]));
        Assert.Equal("fa", LanguageSelection.Choose(config, null, ["nl-NL"]));
    }

    [Fact]
    public async Task Automatic_language_choice_applies_without_persisting_but_explicit_choice_persists()
    {
        var configHandler = new RecordingHandler(Config("Tenant", 1));
        var config = new AppConfig(
            new ClientHttp(new HttpClient(configHandler) { BaseAddress = new Uri("https://tenant.example/") }, new RecordingReloader()),
            new RecordingJs());
        await config.LoadAsync();
        var js = new LanguageJs();
        var translator = new Translator(new ClientHttp(
            new HttpClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })) { BaseAddress = new Uri("https://tenant.example/") },
            new RecordingReloader()));
        var selection = new LanguageSelection(config, translator, js);

        await selection.InitializeAsync();

        Assert.Equal("en", selection.CurrentCode);
        Assert.Contains("quesshi.applyLanguage", js.Calls);
        Assert.DoesNotContain("quesshi.setLanguage", js.Calls);

        await selection.UseAsync("en");

        Assert.Contains("quesshi.setLanguage", js.Calls);
    }

    [Fact]
    public async Task Tenant_language_selection_preserves_right_to_left_direction_for_the_admin_shell()
    {
        var config = new AppConfig(
            new ClientHttp(new HttpClient(new RecordingHandler(Config("Tenant", 1, "fa", "rtl"))) { BaseAddress = new Uri("https://tenant.example/") }, new RecordingReloader()),
            new RecordingJs());
        await config.LoadAsync();
        var js = new LanguageJs();
        var translator = new Translator(new ClientHttp(
            new HttpClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })) { BaseAddress = new Uri("https://tenant.example/") },
            new RecordingReloader()));
        var selection = new LanguageSelection(config, translator, js);

        await selection.InitializeAsync();

        Assert.Equal("rtl", js.AppliedDirections.Single());
    }

    [Fact]
    public void Worker_keeps_tenant_resources_out_of_integrity_assets_and_handles_304_offline()
    {
        var worker = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "service-worker.published.js"));
        Assert.Contains("dynamicPaths", worker);
        Assert.Contains("'/theme.css'", worker);
        Assert.Contains("'/manifest.webmanifest'", worker);
        Assert.Contains("'/api/config'", worker);
        Assert.Contains("response.status === 304 && cached", worker);
        Assert.Contains("return cached || Response.error()", worker);
        Assert.Contains("!dynamicPaths.has", worker);
        Assert.Contains("!asset.url.includes('Quesshi.Web.Admin')", worker);
    }

    [Fact]
    public void Published_server_maps_unmatched_tenant_routes_to_the_blazor_shell()
    {
        var platform = FindPlatformDirectory();
        var server = File.ReadAllText(Path.Combine(platform, "src", "Quesshi.Server", "Program.cs"));

        Assert.Contains("app.MapStaticAssets(staticAssetsManifest);", server);
        Assert.Contains("app.MapFallbackToFile(\"index.html\");", server);
        Assert.True(server.IndexOf("app.MapStaticAssets(staticAssetsManifest);", StringComparison.Ordinal) < server.IndexOf("app.MapFallbackToFile(\"index.html\");", StringComparison.Ordinal));
    }

    private static string FindPlatformDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var platform = Path.Combine(directory.FullName, "platform");
            if (File.Exists(Path.Combine(platform, "Quesshi.Platform.slnx"))) return platform;
        }

        throw new DirectoryNotFoundException("Could not locate the platform source directory.");
    }

    private static HttpResponseMessage Config(string name, long version, string languageCode = "en", string direction = "ltr") => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"name":"{{name}}","languages":[{"code":"{{languageCode}}","displayName":"Language","direction":"{{direction}}"}],"defaultLanguage":"{{languageCode}}","capabilities":{},"configVersion":{{version}},"serverBuildId":"test"}""", Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class RecordingReloader : IClientReloader
    {
        public List<string> Builds { get; } = [];
        public Task ReloadOnceAsync(string buildId) { Builds.Add(buildId); return Task.CompletedTask; }
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Record<TValue>(identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => Record<TValue>(identifier);
        private ValueTask<TValue> Record<TValue>(string identifier) { Calls.Add(identifier); return ValueTask.FromResult(default(TValue)!); }
    }

    private sealed class LanguageJs : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        public List<string?> AppliedDirections { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "quesshi.applyLanguage") AppliedDirections.Add(args?[1] as string);
            object? value = identifier switch
            {
                "quesshi.get" => null,
                "quesshi.browserLanguages" => new[] { "en-US" },
                _ => null
            };
            return ValueTask.FromResult((TValue)value!);
        }
    }
}
