using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Plugin.Api;
using Quesshi.Testing;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Server.Tests;

/// <summary>
/// The pack editor page against the real host: it is rendered by the web client's own code and its
/// calls go to the real <c>/api/packs</c> endpoints, so the order it persists is the one the server stores.
/// </summary>
public sealed class PackEditorHostTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new PackEditorFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("switch", "custom_questions", true);
        await _host.SetCapabilityAsync("kind", "choice", true);
        await _host.ExecuteOwnerAsync("""
            insert into tenant_languages(tenant_id, code, name, english_name, direction, sort_order)
            values (@tenantId, 'en', 'English', 'English', 'ltr', 0)
            """, new { tenantId = _host.TenantId });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Reordering_questions_in_the_editor_persists_in_the_real_api_and_after_reload()
    {
        var pack = await CreatePackAsync("Ordered");
        var first = await AddQuestionAsync(pack, "First");
        var second = await AddQuestionAsync(pack, "Second");
        var third = await AddQuestionAsync(pack, "Third");

        using (var editor = new EditorBrowser(Host.Player, Host.PlayerId, pack))
        {
            Assert.Equal([first, second, third], editor.Order());
            await editor.Page.InvokeAsync(() => editor.Page.Find($"[data-question='{third}'] [data-action='up']").ClickAsync());
            editor.Page.WaitForAssertion(() => Assert.Equal([first, third, second], editor.Order()), Wait);
        }

        var stored = await Host.Player.GetFromJsonAsync<List<StoredQuestion>>($"/api/packs/{pack}/questions");
        Assert.Equal([first, third, second], stored!.Select(question => question.Id));
        using var reloaded = new EditorBrowser(Host.Player, Host.PlayerId, pack);
        Assert.Equal([first, third, second], reloaded.Order());
        Assert.Equal(["First", "Third", "Second"], reloaded.Page.FindAll("[data-field='prompt']").Select(e => e.GetAttribute("value")));
    }

    private async Task<Guid> CreatePackAsync(string title)
    {
        using var response = await Host.Player.PostAsJsonAsync("/api/packs", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StoredPack>())!.Id;
    }

    private async Task<Guid> AddQuestionAsync(Guid pack, string prompt)
    {
        using var response = await Host.Player.PostAsJsonAsync($"/api/packs/{pack}/questions", new
        {
            kind = "choice",
            prompt,
            payload = new { options = new[] { "A", "B", "C", "D" }, correctIndex = 1 },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<StoredQuestion>())!.Id;
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private sealed record StoredPack(Guid Id);

    private sealed record StoredQuestion(Guid Id);

    /// <summary>One browser on the editor: the real page rendered with the web client's own services talking to the real host.</summary>
    private sealed class EditorBrowser : IDisposable
    {
        private readonly BunitContext _context = new();

        public EditorBrowser(HttpClient client, Guid playerId, Guid pack)
        {
            var http = new ClientHttp(new HttpClient(new Bridge(client, playerId)) { BaseAddress = new Uri("http://tenant.test/") }, new NoReloader());
            _context.JSInterop.Mode = JSRuntimeMode.Loose;
            var config = new AppConfig(http, _context.JSInterop.JSRuntime);
            config.LoadAsync().GetAwaiter().GetResult();
            var translator = new Translator(http);
            translator.UseAsync("en").GetAwaiter().GetResult();
            var api = new PlayerApi(http);
            var auth = new AuthState(api);
            auth.InitializeAsync().GetAwaiter().GetResult();
            _context.Services.AddSingleton(config);
            _context.Services.AddSingleton(translator);
            _context.Services.AddSingleton(auth);
            _context.Services.AddSingleton(api);
            _context.Services.AddSingleton(new PackAvailability(config, auth));
            _context.Services.AddSingleton(new PackApi(api));
            var registry = new ClientRegistry(new NoLoader());
            new WebPluginsEntry().Register(registry);
            _context.Services.AddSingleton<IClientRegistry>(registry);
            Page = _context.Render<PackEditor>(p => p.Add(c => c.Id, pack));
            Page.WaitForAssertion(() => Assert.NotEmpty(Page.FindAll("[data-question]")), Wait);
        }

        public IRenderedComponent<PackEditor> Page { get; }

        public Guid[] Order() => [.. Page.FindAll("[data-question]").Select(e => Guid.Parse(e.GetAttribute("data-question")!))];

        public void Dispose() => _context.Dispose();
    }

    private sealed class Bridge(HttpClient inner, Guid playerId) : HttpMessageHandler
    {
        private readonly Guid _playerId = playerId;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwarded = new HttpRequestMessage(request.Method, new Uri(inner.BaseAddress!, request.RequestUri!.PathAndQuery)) { Content = request.Content };
            foreach (var header in request.Headers.Where(header => !string.Equals(header.Key, "X-CSRF", StringComparison.OrdinalIgnoreCase)))
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
#if QUESSHI_TEST_IDENTITY
            forwarded.Headers.Add("X-Quesshi-Test-Player", _playerId.ToString());
#endif
            return inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }

    private sealed class NoReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class PackEditorFeature : IFeature
    {
        public string Id => "pack_editor";
        public FeatureSettings Settings => FeatureSettings.Create(new PackEditorSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record PackEditorSettings;
}
