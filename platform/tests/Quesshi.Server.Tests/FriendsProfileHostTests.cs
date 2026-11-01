using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Features.Features.Friends;
using Quesshi.Plugin.Api;
using Quesshi.Testing;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Server.Tests;

/// <summary>
/// The player Profile with its friends section against the real host: the page and the section's own client
/// run the web client's code, and every list, search, add and remove goes to the real friends endpoints
/// (anti-forgery handshake included). The assertions read the real list endpoint, not a fake's state.
/// </summary>
public sealed class FriendsProfileHostTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new FriendsFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "friends", true);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task The_profile_lists_the_real_friend_names_avatars_and_online_marks()
    {
        var online = await FriendAsync("Ada Online");
        var offline = await FriendAsync("Bo Offline");
        await Host.ExecuteOwnerAsync("update players set last_seen_at = now() where tenant_id = @t and id = @id",
            new { t = Host.TenantId, id = online });
        await Host.ExecuteOwnerAsync("update players set last_seen_at = now() - interval '1 hour' where tenant_id = @t and id = @id",
            new { t = Host.TenantId, id = offline });
        using var browser = new ProfileBrowser(Host.Player, Host.PlayerId);

        browser.Page.WaitForAssertion(() => Assert.Equal(2, browser.Page.FindAll("[data-test='friend']").Count), Wait);

        var rows = browser.Page.FindAll("[data-test='friend']");
        Assert.Equal(["Ada Online", "Bo Offline"], rows.Select(r => r.QuerySelector("[data-test='friend-name']")!.TextContent.Trim()));
        Assert.All(rows, r => Assert.Equal("seed-" + r.QuerySelector("[data-test='friend-name']")!.TextContent.Trim()[..2].ToLowerInvariant(),
            r.QuerySelector("[data-test='friend-avatar']")!.GetAttribute("data-seed")));
        Assert.Single(rows[0].QuerySelectorAll("[data-test='friend-online']"));
        Assert.Empty(rows[1].QuerySelectorAll("[data-test='friend-online']"));
    }

    [SkippableFact]
    public async Task Adding_a_search_result_from_the_profile_makes_the_real_friends_endpoint_contain_it()
    {
        var (_, candidate) = await Host.CreateSecondPlayerClientAsync();
        await Rename(candidate, "Zanzibar Quill", "seed-zq");
        using var browser = new ProfileBrowser(Host.Player, Host.PlayerId);
        browser.Page.WaitForElement("[data-test='friends-section']", Wait);

        browser.Page.Find("[data-test='friends-search']").Input("zanzib");
        browser.Page.Find("form").Submit();
        browser.Page.WaitForAssertion(() => Assert.Single(browser.Page.FindAll("[data-test='friends-candidate']")), Wait);
        await browser.Page.InvokeAsync(() => browser.Page.Find("[data-test='friends-add']").ClickAsync());

        browser.Page.WaitForAssertion(() => Assert.Single(browser.Page.FindAll("[data-test='friend']")), Wait);
        Assert.Equal([candidate], (await ListAsync()).Select(f => f.Id));
        Assert.Equal("Zanzibar Quill", browser.Page.Find("[data-test='friend-name']").TextContent.Trim());
        Assert.Empty(browser.Page.FindAll("[data-test='friends-candidate']"));
    }

    [SkippableFact]
    public async Task Removing_a_friend_from_the_profile_makes_the_real_friends_endpoint_omit_it()
    {
        var keep = await FriendAsync("Keep Me");
        var drop = await FriendAsync("Drop Me");
        using var browser = new ProfileBrowser(Host.Player, Host.PlayerId);
        browser.Page.WaitForAssertion(() => Assert.Equal(2, browser.Page.FindAll("[data-test='friend']").Count), Wait);

        var row = browser.Page.FindAll("[data-test='friend']").Single(r => r.TextContent.Contains("Drop Me"));
        await browser.Page.InvokeAsync(() => row.QuerySelector("[data-test='friend-remove']")!.ClickAsync());

        browser.Page.WaitForAssertion(() => Assert.Single(browser.Page.FindAll("[data-test='friend']")), Wait);
        var remaining = (await ListAsync()).Select(f => f.Id).ToArray();
        Assert.Equal([keep], remaining);
        Assert.DoesNotContain(drop, remaining);
    }

    [SkippableFact]
    public async Task The_profile_has_no_friends_section_when_the_real_tenant_has_friends_disabled()
    {
        await Host.SetCapabilityAsync("feature", "friends", false);
        using var disabled = await Host.Player.GetAsync("/api/x/friends");
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);

        using var browser = new ProfileBrowser(Host.Player, Host.PlayerId);

        browser.Page.WaitForAssertion(() => Assert.NotNull(browser.Page.Find("#profile-title")), Wait);
        await Task.Delay(500);
        Assert.Empty(browser.Page.FindAll("[data-test='friends-section']"));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    /// <summary>A second player, named and made a friend of the main player through the real add endpoint.</summary>
    private async Task<Guid> FriendAsync(string name)
    {
        var (_, id) = await Host.CreateSecondPlayerClientAsync();
        await Rename(id, name, "seed-" + name[..2].ToLowerInvariant());
        using var response = await Host.Player.PostAsync($"/api/x/friends/{id}", content: null);
        response.EnsureSuccessStatusCode();
        return id;
    }

    private Task Rename(Guid id, string name, string seed) => Host.ExecuteOwnerAsync(
        "update players set display_name = @name, avatar_seed = @seed where tenant_id = @t and id = @id",
        new { t = Host.TenantId, id, name, seed });

    private async Task<FriendListEntry[]> ListAsync() =>
        await Host.Player.GetFromJsonAsync<FriendListEntry[]>("/api/x/friends", new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

    private sealed class ProfileBrowser : IDisposable
    {
        private readonly BunitContext _context = new();

        public ProfileBrowser(HttpClient client, Guid playerId)
        {
            var http = new ClientHttp(new HttpClient(new Bridge(client, playerId)) { BaseAddress = new Uri("http://tenant.test/") }, new NoReloader());
            _context.JSInterop.Mode = JSRuntimeMode.Loose;
            var translator = new Translator(http);
            translator.UseAsync("en").GetAwaiter().GetResult();
            var auth = new AuthState(new PlayerApi(http));
            auth.InitializeAsync().GetAwaiter().GetResult();
            _context.Services.AddSingleton(http);
            _context.Services.AddSingleton(translator);
            _context.Services.AddSingleton<IClientTranslator>(translator);
            _context.Services.AddSingleton(auth);
            var registry = new ClientRegistry(new NoLoader());
            new WebPluginsEntry().RegisterSlots(registry);
            _context.Services.AddSingleton<IFormatClientRegistry>(registry);
            Page = _context.Render<Profile>();
        }

        public IRenderedComponent<Profile> Page { get; }

        public void Dispose() => _context.Dispose();
    }

    /// <summary>Carries the web client's relative requests to the real host through a fixture client that already holds its identity.
    /// The fixture client attaches its own anti-forgery token, so the section's token header is dropped here (as in the lobby tests);
    /// the section still performs its /api/antiforgery handshake against the real host.</summary>
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
}
