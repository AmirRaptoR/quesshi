using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Plugin.Api;
using Quesshi.Testing;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Server.Tests;

/// <summary>
/// The platform Home against the real host: the page is rendered by the web client's own code and its
/// config, inbox, create, join and invitation calls go to the real endpoints, so the session ids and
/// seats it relies on come from the real server rather than from an assumption about it.
/// </summary>
public sealed class HomeHostTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new HomeFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("format", "async_duel", true);
        await _host.SetCapabilityAsync("kind", "choice", true);
        await _host.ExecuteOwnerAsync("""
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
            values (@t, 'source', 'bank', true, true, '{}'::jsonb)
            on conflict (tenant_id, type, key) do update set entitled = true, enabled = true
            """, new { t = _host.TenantId });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Creating_from_home_returns_the_id_of_the_session_the_real_server_created()
    {
        // The real /api/config does not yet list formats, so Home's create buttons are covered by the rendered-page
        // tests; this drives the same HomeSessions call the buttons make against the real endpoint.
        using var home = new HomeBrowser(Host.Player, Host.PlayerId);

        var outcome = await home.Sessions.CreateAsync("async_duel");

        var created = Assert.IsType<Guid>(outcome.SessionId);
        Assert.Equal(1, (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from sessions where id = @id and status = 'lobby'", new { id = created })).Single());
        Assert.Equal(1, await ActiveSeats(created, Host.PlayerId));
    }

    [SkippableFact]
    public async Task Joining_from_home_by_a_lower_case_code_seats_the_player_and_opens_the_joined_session()
    {
        var (secondClient, secondId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await CreateLobbyAsync(Host.Player);
        using var home = new HomeBrowser(secondClient, secondId);

        home.Page.Find("input[name='code']").Input(lobby.Code.ToLowerInvariant());
        await home.Page.Find("[data-test='join-submit']").ClickAsync();

        Assert.Equal(lobby.Id, home.OpenedSession());
        Assert.Equal(1, await ActiveSeats(lobby.Id, secondId));
    }

    [SkippableFact]
    public async Task Accepting_an_invitation_from_home_seats_the_recipient_and_opens_the_session()
    {
        var (recipient, recipientId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await InviteAsync(recipientId);
        using var home = new HomeBrowser(recipient, recipientId);
        Assert.Equal(0, await ActiveSeats(lobby, recipientId));

        home.Page.WaitForElement($"[data-invitation='{lobby}']", Wait);
        await home.Page.InvokeAsync(() => home.Page.Find($"[data-invitation='{lobby}'] [data-action='accept']").ClickAsync());

        Assert.Equal(lobby, home.OpenedSession());
        Assert.Equal(1, await ActiveSeats(lobby, recipientId));
        Assert.Equal(0, await PendingInvites(lobby, recipientId));
    }

    [SkippableFact]
    public async Task Declining_an_invitation_from_home_removes_its_card_without_seating_the_player()
    {
        var (recipient, recipientId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await InviteAsync(recipientId);
        using var home = new HomeBrowser(recipient, recipientId);

        home.Page.WaitForElement($"[data-invitation='{lobby}']", Wait);
        await home.Page.InvokeAsync(() => home.Page.Find($"[data-invitation='{lobby}'] [data-action='decline']").ClickAsync());

        home.Page.WaitForAssertion(() => Assert.Empty(home.Page.FindAll($"[data-invitation='{lobby}']")), Wait);
        Assert.Equal(0, await ActiveSeats(lobby, recipientId));
        Assert.Equal(0, await PendingInvites(lobby, recipientId));
        Assert.EndsWith("/", home.Navigation.Uri);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    /// <summary>A lobby owned by the main player that has invited <paramref name="recipient"/>, a recent co-player.</summary>
    private async Task<Guid> InviteAsync(Guid recipient)
    {
        var earlier = await CreateLobbyAsync(Host.Player);
        await Host.ExecuteOwnerAsync("""
            insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
            values (@tenantId, @sessionId, @playerId, 1, 'player', 'active', 'idle', -1, 0)
            """, new { tenantId = Host.TenantId, sessionId = earlier.Id, playerId = recipient });
        var lobby = await CreateLobbyAsync(Host.Player);
        var invited = await Host.Player.PostAsJsonAsync($"/api/sessions/{lobby.Id}/invites", new { playerId = recipient });
        Assert.True(invited.IsSuccessStatusCode, await invited.Content.ReadAsStringAsync());
        return lobby.Id;
    }

    private static async Task<(Guid Id, string Code)> CreateLobbyAsync(HttpClient client)
    {
        var id = Guid.NewGuid();
        var created = await client.PostAsJsonAsync("/api/sessions", new
        {
            id,
            format = "async_duel",
            content = new { source = "bank", parameters = new { }, count = 10 },
            rules = new { },
            capacity = 4,
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return (id, document.RootElement.GetProperty("code").GetString()!);
    }

    private async Task<int> ActiveSeats(Guid id, Guid player) => (await Host.QueryOwnerAsync<int>(
        "select count(*)::int from session_participants where session_id = @id and player_id = @player and status = 'active'", new { id, player })).Single();

    private async Task<int> PendingInvites(Guid id, Guid player) => (await Host.QueryOwnerAsync<int>(
        "select count(*)::int from session_invites where session_id = @id and player_id = @player", new { id, player })).Single();

    /// <summary>One browser on Home: the real page rendered with the web client's own services talking to the real host.</summary>
    private sealed class HomeBrowser : IDisposable
    {
        private readonly BunitContext _context = new();

        public HomeBrowser(HttpClient client, Guid playerId)
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
            var state = new PlatformState(config);
            _context.Services.AddSingleton(state);
            Sessions = new HomeSessions(api, state, config);
            var registry = new ClientRegistry(new NoLoader());
            registry.RegisterFormat(new DuelFormat());
            _context.Services.AddSingleton<IFormatClientRegistry>(registry);
            Page = _context.Render<Home>();
            Page.WaitForAssertion(() => Assert.NotEmpty(Page.FindAll("input[name='code']")), Wait);
        }

        public IRenderedComponent<Home> Page { get; }

        public HomeSessions Sessions { get; }

        public NavigationManager Navigation => _context.Services.GetRequiredService<NavigationManager>();

        public Guid OpenedSession()
        {
            var path = new Uri(Navigation.Uri).AbsolutePath;
            Assert.StartsWith("/s/", path);
            return Guid.Parse(path["/s/".Length..]);
        }

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

    private sealed class Screen : ComponentBase { }

    private sealed class DuelFormat : IFormatClient
    {
        public string Id => "async_duel";
        public Type Settings => typeof(Screen);
        public Type Screen => typeof(Screen);
        public Type Result => typeof(Screen);
    }

    private sealed class HomeFeature : IFeature
    {
        public string Id => "home";
        public FeatureSettings Settings => FeatureSettings.Create(new HomeSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record HomeSettings;
}
