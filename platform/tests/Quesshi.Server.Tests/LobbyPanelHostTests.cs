using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Bunit.Extensions.WaitForHelpers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Engine.Contracts;
using Quesshi.Server.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Shared;
using Quesshi.Testing;
using Quesshi.Web.Components;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Server.Tests;

/// <summary>
/// The shared lobby panel against the real host: each "browser" is the web client's own stream reader,
/// shared state and lobby actions talking to the real endpoints, with the panel rendered from what the
/// stream delivers. Nothing is reloaded between the steps; every change reaches the panel by the stream.
/// </summary>
public sealed class LobbyPanelHostTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-10-23T00:00:00Z"));
    private readonly List<Browser> _browsers = [];
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new LobbyPanelFeature()])
        {
            ConfigureServices = services => services.AddSingleton<IClock>(_clock),
        };
        await _host.StartAsync();
        await _host.SetCapabilityAsync("format", "async_duel", true);
        await _host.SetCapabilityAsync("kind", "choice", true);
        var category = Guid.NewGuid();
        await _host.ExecuteOwnerAsync("""
            insert into categories(tenant_id, id, slug, names) values (@t, @category, 'panel-category', '{"en":"Panel"}'::jsonb);
            insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
            values (@t, 'source', 'bank', true, true, jsonb_build_object('CategoriesByFamily', jsonb_build_object('trivia', jsonb_build_array(@category::text))))
            on conflict (tenant_id, type, key) do update set entitled = true, enabled = true, settings = excluded.settings;
            insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
            select @t, gen_random_uuid(), 'choice', 1, 'trivia', 'en', @category, 1 + (n % 5), 'Panel prompt ' || n,
                   '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, 'panel-' || n, 'approved', 'admin'
            from generate_series(1, 60) as n
            """, new { t = _host.TenantId, category });
    }

    public async Task DisposeAsync()
    {
        foreach (var browser in _browsers) await browser.DisposeAsync();
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_second_browser_joining_appears_in_the_first_browsers_roster_without_a_reload()
    {
        var (secondClient, secondId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await CreateLobbyAsync();
        var first = await OpenAsync(Host.Player, Host.PlayerId, lobby.Id);
        first.Panel.WaitForAssertion(() => Assert.Single(first.Panel.FindAll("[data-seat]")), Wait);

        _ = await JoinAsync(secondClient, lobby.Code);
        var second = await OpenAsync(secondClient, secondId, lobby.Id);

        first.Panel.WaitForAssertion(() => Assert.Equal([Host.PlayerId.ToString(), secondId.ToString()],
            first.Panel.FindAll("[data-seat]").Select(seat => seat.GetAttribute("data-seat"))), Wait);
        Assert.NotNull(second);
    }

    [SkippableFact]
    public async Task A_browser_going_offline_is_marked_offline_in_the_other_browsers_roster_without_a_reload()
    {
        var (first, second, secondId) = await TwoBrowsersOnlineAsync();

        await second.GoOfflineAsync();
        await WaitForAsync(() => Hub.SubscriptionCount == 1);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await Hub.ReconcileAsync();

        first.Panel.WaitForAssertion(() => Assert.Equal("false", first.Panel.Find($"[data-seat='{secondId}']").GetAttribute("data-online")), Wait);
        Assert.Equal("true", first.Panel.Find($"[data-seat='{Host.PlayerId}']").GetAttribute("data-online"));
    }

    [SkippableFact]
    public async Task A_reconnecting_browser_is_marked_online_again_in_the_other_browsers_roster_without_a_reload()
    {
        var (first, second, secondId) = await TwoBrowsersOnlineAsync();
        await second.GoOfflineAsync();
        await WaitForAsync(() => Hub.SubscriptionCount == 1);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await Hub.ReconcileAsync();
        first.Panel.WaitForAssertion(() => Assert.Equal("false", first.Panel.Find($"[data-seat='{secondId}']").GetAttribute("data-online")), Wait);

        await second.GoOnlineAsync();
        await WaitForAsync(() => Hub.SubscriptionCount == 2);
        _clock.Advance(PresenceTiming.RefreshInterval);
        await Hub.ReconcileAsync();

        first.Panel.WaitForAssertion(() => Assert.Equal("true", first.Panel.Find($"[data-seat='{secondId}']").GetAttribute("data-online")), Wait);
    }

    [SkippableFact]
    public async Task Inviting_an_eligible_recent_co_player_from_the_lobby_sends_one_invitation_through_the_real_endpoint()
    {
        var (_, coPlayerId) = await Host.CreateSecondPlayerClientAsync();
        var earlier = await CreateLobbyAsync();
        await Host.ExecuteOwnerAsync("""
            insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
            values (@tenantId, @sessionId, @playerId, 1, 'player', 'active', 'idle', -1, 0)
            """, new { tenantId = Host.TenantId, sessionId = earlier.Id, playerId = coPlayerId });
        var lobby = await CreateLobbyAsync();
        var owner = await OpenAsync(Host.Player, Host.PlayerId, lobby.Id, [new LobbyInvitee(coPlayerId.ToString(), "Co-player")]);
        owner.Panel.WaitForAssertion(() => Assert.Single(owner.Panel.FindAll("[data-seat]")), Wait);

        owner.Panel.Find($"[data-invitee='{coPlayerId}']").Click();

        owner.Panel.WaitForAssertion(() => Assert.Contains("lobby.invite.invited", owner.Panel.Find($"[data-invitee='{coPlayerId}']").TextContent), Wait);
        Assert.Equal(1, (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from session_invites where session_id = @sessionId and player_id = @playerId",
            new { sessionId = lobby.Id, playerId = coPlayerId })).Single());
        Assert.Empty(owner.Panel.FindAll("[role='alert']"));
    }

    [SkippableFact]
    public async Task The_seated_owner_starts_an_eligible_lobby_through_the_real_endpoint()
    {
        var (secondClient, secondId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await CreateLobbyAsync();
        _ = await JoinAsync(secondClient, lobby.Code);
        var owner = await OpenAsync(Host.Player, Host.PlayerId, lobby.Id);
        owner.Panel.WaitForAssertion(() => Assert.False(owner.Panel.Find("[data-lobby-start]").HasAttribute("disabled")), Wait);

        owner.Panel.Find("[data-lobby-start]").Click();

        await WaitForAsync(() => Status(lobby.Id).GetAwaiter().GetResult() == "running");
        Assert.Equal(2, (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from session_participants where session_id = @id and status = 'active'", new { id = lobby.Id })).Single());
        Assert.NotEqual(Guid.Empty, secondId);
    }

    [SkippableFact]
    public async Task A_seated_non_owner_leaves_the_lobby_through_the_real_endpoint_and_the_owner_sees_the_seat_go()
    {
        var (secondClient, secondId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await CreateLobbyAsync();
        _ = await JoinAsync(secondClient, lobby.Code);
        var owner = await OpenAsync(Host.Player, Host.PlayerId, lobby.Id);
        var guest = await OpenAsync(secondClient, secondId, lobby.Id);
        guest.Panel.WaitForAssertion(() => Assert.Equal(2, guest.Panel.FindAll("[data-seat]").Count), Wait);
        Assert.Empty(guest.Panel.FindAll("[data-lobby-start]"));

        guest.Panel.Find("[data-lobby-leave]").Click();

        await WaitForAsync(() => ActiveSeats(lobby.Id, secondId).GetAwaiter().GetResult() == 0);
        owner.Panel.WaitForAssertion(() => Assert.Single(owner.Panel.FindAll("[data-seat]")), Wait);
        Assert.Equal("lobby", await Status(lobby.Id));
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private StreamHub Hub => Host.Services.GetRequiredService<StreamHub>();

    private async Task<(Browser First, Browser Second, Guid SecondId)> TwoBrowsersOnlineAsync()
    {
        var (secondClient, secondId) = await Host.CreateSecondPlayerClientAsync();
        var lobby = await CreateLobbyAsync();
        _ = await JoinAsync(secondClient, lobby.Code);
        var first = await OpenAsync(Host.Player, Host.PlayerId, lobby.Id);
        var second = await OpenAsync(secondClient, secondId, lobby.Id);
        await WaitForAsync(() => Hub.SubscriptionCount == 2);
        await ReconcileUntilAsync(first.Panel, () =>
        {
            Assert.Equal("true", first.Panel.Find($"[data-seat='{secondId}']").GetAttribute("data-online"));
            Assert.Equal("true", first.Panel.Find($"[data-seat='{Host.PlayerId}']").GetAttribute("data-online"));
        });
        return (first, second, secondId);
    }

    /// <summary>Lets the presence loop run (one refresh interval apart) until the browser's panel shows the expected marks.</summary>
    private async Task ReconcileUntilAsync(IRenderedComponent<Harness> panel, Action expected)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            _clock.Advance(PresenceTiming.RefreshInterval);
            await Hub.ReconcileAsync();
            try
            {
                panel.WaitForAssertion(expected, TimeSpan.FromSeconds(1));
                return;
            }
            catch (WaitForFailedException) when (attempt < 9) { }
        }
    }

    private async Task<Browser> OpenAsync(HttpClient client, Guid playerId, Guid sessionId, IReadOnlyList<LobbyInvitee>? invitees = null)
    {
        var browser = new Browser(client, playerId, sessionId, invitees ?? []);
        _browsers.Add(browser);
        await browser.GoOnlineAsync();
        return browser;
    }

    private async Task<(Guid Id, string Code)> CreateLobbyAsync()
    {
        var id = Guid.NewGuid();
        var created = await Host.Player.PostAsJsonAsync("/api/sessions", new
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

    private static async Task<HttpStatusCode> JoinAsync(HttpClient client, string code)
    {
        var response = await client.PostAsJsonAsync("/api/sessions/join", new { code });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return response.StatusCode;
    }

    private async Task<string> Status(Guid id) =>
        (await Host.QueryOwnerAsync<string>("select status from sessions where id = @id", new { id })).Single();

    private async Task<int> ActiveSeats(Guid id, Guid player) => (await Host.QueryOwnerAsync<int>(
        "select count(*)::int from session_participants where session_id = @id and player_id = @player and status = 'active'", new { id, player })).Single();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The expected state was not reached.");
    }

    /// <summary>One browser tab: the web client's stream reader and shared state feeding a rendered lobby panel.</summary>
    private sealed class Browser : IAsyncDisposable
    {
        private readonly StreamClient _stream;
        private readonly Guid _sessionId;
        private readonly BunitContext _context = new();

        public Browser(HttpClient client, Guid playerId, Guid sessionId, IReadOnlyList<LobbyInvitee> invitees)
        {
            _sessionId = sessionId;
            var http = new ClientHttp(new HttpClient(new Bridge(client, playerId)) { BaseAddress = new Uri("http://tenant.test/") }, new NoReloader());
            _stream = new StreamClient(http);
            var state = new PlatformState(new AppConfig(http, null!), _stream);
            _context.Services.AddSingleton(state);
            _context.Services.AddSingleton(new Translator(http));
            _context.Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
            _context.Services.AddSingleton<IFormatClientRegistry>(new ClientRegistry(new NoLoader()));
            _context.JSInterop.Mode = JSRuntimeMode.Loose;
            Panel = _context.Render<Harness>(p => p.Add(c => c.Invitees, invitees));
        }

        public IRenderedComponent<Harness> Panel { get; }

        public Task GoOnlineAsync() => _stream.SyncAsync(true, _sessionId);

        public Task GoOfflineAsync() => _stream.SyncAsync(false, null);

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            await _context.DisposeAsync();
        }
    }

    /// <summary>The hosting page's job: pass each newer accepted view to the panel.</summary>
    private sealed class Harness : ComponentBase, IDisposable
    {
        private SessionView? _view;
        [Inject] private PlatformState State { get; set; } = default!;
        [Parameter] public IReadOnlyList<LobbyInvitee> Invitees { get; set; } = [];

        protected override void OnInitialized()
        {
            _view = State.View;
            State.ViewChanged += OnViewChanged;
        }

        private void OnViewChanged(SessionView? previous, SessionView view) =>
            _ = InvokeAsync(() =>
            {
                _view = view;
                StateHasChanged();
            });

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (_view is null) return;
            builder.OpenComponent<LobbyPanel>(0);
            builder.AddComponentParameter(1, nameof(LobbyPanel.SessionView), _view);
            builder.AddComponentParameter(2, nameof(LobbyPanel.Invitees), Invitees);
            builder.CloseComponent();
        }

        public void Dispose() => State.ViewChanged -= OnViewChanged;
    }

    /// <summary>Carries the web client's relative requests to the real host through a fixture client that already holds its identity.</summary>
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

    private sealed class LobbyPanelFeature : IFeature
    {
        public string Id => "lobby_panel";
        public FeatureSettings Settings => FeatureSettings.Create(new LobbyPanelSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record LobbyPanelSettings;
}
