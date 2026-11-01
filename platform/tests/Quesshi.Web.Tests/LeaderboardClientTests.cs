using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Features.Leaderboard;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class LeaderboardClientTests : BunitContext
{
    private static readonly Guid Me = Guid.Parse("21ac7193-52fd-4d4b-9194-5cd547448072");
    private const string SessionId = "7f3c1d52-0000-4000-8000-000000000001";

    private readonly Server server = new();

    [Fact]
    public void The_ranks_page_lists_rank_name_avatar_and_score_of_the_selected_board()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='ranks-row']").Count));
        var first = cut.FindAll("[data-test='ranks-row']")[0];
        Assert.Equal("1", first.QuerySelector("[data-test='rank']")!.TextContent);
        Assert.Equal("Sara", first.QuerySelector("[data-test='name']")!.TextContent);
        Assert.Equal("seed-sara", first.QuerySelector("[data-test='avatar']")!.GetAttribute("data-seed"));
        Assert.Equal("900", first.QuerySelector("[data-test='score']")!.TextContent);
        Assert.Contains("/api/x/leaderboard/all?period=all&scope=all", server.Requests);
    }

    [Fact]
    public void Changing_the_board_reloads_the_list_for_that_board()
    {
        var cut = RenderPage(formats: ["live"]);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='ranks-row']").Count));

        cut.Find("[data-test='ranks-board']").Change("live");

        cut.WaitForAssertion(() => Assert.Equal("Live champion", cut.Find("[data-test='name']").TextContent));
        Assert.Contains("/api/x/leaderboard/live?period=all&scope=all", server.Requests);
    }

    [Fact]
    public void Changing_the_period_reloads_the_list_for_that_period()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='ranks-row']").Count));
        var month = cut.FindAll("[data-test='ranks-period'] option")[1].GetAttribute("value")!;

        cut.Find("[data-test='ranks-period']").Change(month);

        cut.WaitForAssertion(() => Assert.Equal("Month champion", cut.Find("[data-test='name']").TextContent));
        Assert.Contains($"/api/x/leaderboard/all?period={month}&scope=all", server.Requests);
        Assert.Matches(@"^\d{4}-\d{2}$", month);
        Assert.Matches(@"^\d{4}-W\d{2}$", cut.FindAll("[data-test='ranks-period'] option")[2].GetAttribute("value")!);
    }

    [Fact]
    public void The_ranks_page_shows_the_callers_own_row_and_rank()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='ranks-me']")));
        Assert.Equal("14", cut.Find("[data-test='me-rank']").TextContent);
        Assert.Equal("Amir", cut.Find("[data-test='me-name']").TextContent);
        Assert.Equal("120", cut.Find("[data-test='me-score']").TextContent);
    }

    [Fact]
    public void The_friends_tab_is_not_rendered_when_the_server_answers_unavailable()
    {
        server.FriendsAvailable = false;

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='ranks-row']").Count));
        Assert.Empty(cut.FindAll("[data-test='ranks-friends-tab']"));
    }

    [Fact]
    public void The_friends_tab_is_rendered_and_loads_the_friends_scope_when_available()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='ranks-friends-tab']")));

        cut.Find("[data-test='ranks-friends-tab']").Click();

        cut.WaitForAssertion(() => Assert.Equal("Friend", cut.Find("[data-test='name']").TextContent));
    }

    [Fact]
    public void The_plugin_registers_a_nav_item_and_a_home_tile_that_link_to_the_ranks_page()
    {
        var registry = new ClientRegistry(new NoLoader());
        LeaderboardClient.Register(registry);
        Services.AddSingleton<IFormatClientRegistry>(registry);
        Services.AddSingleton<IClientTranslator>(new Keys());

        var nav = Render<ClientSlotHost<NavItemParameters>>(p => p
            .Add(c => c.Slot, ClientSlots.NavItem).Add(c => c.Value, new NavItemParameters("")));
        var tile = Render<ClientSlotHost<HomeTileParameters>>(p => p
            .Add(c => c.Slot, ClientSlots.HomeTile).Add(c => c.Value, new HomeTileParameters(["live"])));

        Assert.Equal("/ranks", nav.Find("[data-test='nav-ranks']").GetAttribute("href"));
        Assert.Equal("/ranks", tile.Find("[data-test='home-tile-ranks']").GetAttribute("href"));
        Assert.Contains(registry.Pages, page => page.Path == "/ranks" && page.Component == typeof(LeaderboardPage));
    }

    [Fact]
    public void The_session_result_shows_a_pending_state_while_the_effects_are_not_done()
    {
        server.EffectStatus = "pending";

        var cut = RenderResult();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='rank-pending']")));
        Assert.Empty(cut.FindAll("[data-test='rank-result']"));
    }

    [Fact]
    public void The_session_result_replaces_the_pending_state_with_the_new_rank_when_effects_become_done()
    {
        server.EffectStatus = "pending";
        var cut = RenderResult();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='rank-pending']")));

        server.EffectStatus = "done";

        cut.WaitForAssertion(() => Assert.Equal("Your new rank: 14", cut.Find("[data-test='rank-result']").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-test='rank-pending']"));
    }

    private IRenderedComponent<LeaderboardPage> RenderPage(string[]? formats = null)
    {
        var registry = new ClientRegistry(new NoLoader());
        foreach (var id in formats ?? []) registry.RegisterFormat(new FakeFormat(id));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        Services.AddSingleton<IClientTranslator>(new Keys());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(Api());
        return Render<LeaderboardPage>();
    }

    private IRenderedComponent<LeaderboardSessionResult> RenderResult()
    {
        Services.AddSingleton<IClientTranslator>(new Keys());
        Services.AddSingleton<TimeProvider>(new FastTime());
        Services.AddSingleton(Api());
        return Render<LeaderboardSessionResult>(p => p.Add(c => c.Parameters, new SessionResultParameters(Session())));
    }

    private LeaderboardApi Api() =>
        new(new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));

    private static SessionView Session() => PlatformState.ParseView(
        $$$"""{"id":"{{{SessionId}}}","code":"ABCD","format":"live","status":"finished","phase":"over","slot":0,"step":0,"version":3,"configVersion":1,"now":"2026-11-01T10:00:00+00:00","deadline":null,"me":{"id":"p","seat":0,"role":"player","status":"active","score":0,"place":null,"outcome":null},"roster":{"total":0,"seats":[]},"content":{},"rules":{},"body":{}}""")!;

    private sealed class Server : HttpMessageHandler
    {
        public ConcurrentQueue<string> RequestLog { get; } = new();
        public IReadOnlyCollection<string> Requests => RequestLog.ToArray();
        public bool FriendsAvailable { get; set; } = true;
        public volatile string EffectStatus = "pending";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            RequestLog.Enqueue(uri.PathAndQuery);
            if (uri.AbsolutePath == $"/api/sessions/{SessionId}/effects")
                return Task.FromResult(Json($$"""{"revision":1,"features":[{"featureId":"leaderboard","status":"{{EffectStatus}}"}]}"""));

            var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
            var board = uri.AbsolutePath.Split('/')[^1];
            if (query["scope"] == "friends")
                return Task.FromResult(FriendsAvailable
                    ? Json(Page(Row(2, "Amir", 40), Row(1, "Friend", 50)))
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error":"unavailable"}""", Encoding.UTF8, "application/json") });
            if (board == "live") return Task.FromResult(Json(Page(null, Row(1, "Live champion", 10))));
            if (query["period"] != "all") return Task.FromResult(Json(Page(null, Row(1, "Month champion", 10))));
            return Task.FromResult(Json(Page(Row(14, "Amir", 120), Row(1, "Sara", 900), Row(2, "Nik", 800))));
        }

        private static string Row(int rank, string name, int score) =>
            $$$"""{"rank":{{{rank}}},"playerId":"{{{Guid.NewGuid()}}}","name":"{{{name}}}","avatar":"seed-{{{name.ToLowerInvariant()}}}","score":{{{score}}}}""";

        private static string Page(string? me, params string[] entries) =>
            $$"""{"entries":[{{string.Join(",", entries)}}],"me":{{me ?? "null"}}}""";

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Returns keys as text, except the one format string the result needs.</summary>
    private sealed class Keys : IClientTranslator
    {
        public string this[string key] => key switch
        {
            "ranks.newRank" => "Your new rank: {0}",
            _ => key,
        };
    }

    /// <summary>Real time with every delay shortened, so the polling loop converges quickly.</summary>
    private sealed class FastTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.FromMilliseconds(10), period);
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class FakeFormat(string id) : IFormatClient
    {
        public string Id => id;
        public Type Settings => typeof(LeaderboardPage);
        public Type Screen => typeof(LeaderboardPage);
        public Type Result => typeof(LeaderboardPage);
    }
}
