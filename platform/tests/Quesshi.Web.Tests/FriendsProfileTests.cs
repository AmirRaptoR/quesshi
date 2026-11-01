using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class FriendsProfileTests : BunitContext
{
    private const string PlayerId = "21ac7193-52fd-4d4b-9194-5cd547448072";
    private const string Ada = "3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0001";
    private const string Bo = "3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0002";
    private const string Cy = "3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0003";

    [Fact]
    public async Task The_profile_lists_friend_names_avatars_and_online_marks()
    {
        var friends = new FriendsServer();
        var cut = await RenderProfileAsync(friends);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='friend']").Count));
        Assert.Equal(["Ada", "Bo"], cut.FindAll("[data-test='friend-name']").Select(n => n.TextContent.Trim()));
        Assert.Equal(["seed-ada", "seed-bo"], cut.FindAll("[data-test='friend-avatar']").Select(a => a.GetAttribute("data-seed")));
        var rows = cut.FindAll("[data-test='friend']");
        Assert.Single(rows[0].QuerySelectorAll("[data-test='friend-online']"));
        Assert.Empty(rows[1].QuerySelectorAll("[data-test='friend-online']"));
    }

    [Fact]
    public async Task A_search_result_can_be_added_and_the_friends_endpoint_then_contains_it()
    {
        var friends = new FriendsServer();
        var cut = await RenderProfileAsync(friends);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='friend']")));

        cut.Find("[data-test='friends-search']").Input("cy");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-test='friends-candidate']")));
        cut.Find("[data-test='friends-add']").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-test='friend']").Count));
        Assert.Contains(Cy, friends.FriendIds);
        Assert.Equal("cy", friends.LastSearch);
        Assert.Empty(cut.FindAll("[data-test='friends-candidate']"));
        Assert.Equal(["csrf"], friends.CsrfHeaders.Distinct());
    }

    [Fact]
    public async Task A_friend_can_be_removed_and_the_friends_endpoint_then_omits_it()
    {
        var friends = new FriendsServer();
        var cut = await RenderProfileAsync(friends);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='friend']").Count));

        cut.FindAll("[data-test='friend-remove']")[0].Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-test='friend']")));
        Assert.DoesNotContain(Ada, friends.FriendIds);
        Assert.Equal("Bo", cut.Find("[data-test='friend-name']").TextContent.Trim());
    }

    [Fact]
    public async Task A_stale_antiforgery_token_is_refreshed_and_the_mutation_retried()
    {
        var friends = new FriendsServer { RejectFirstMutation = true };
        var cut = await RenderProfileAsync(friends);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='friend']").Count));

        cut.FindAll("[data-test='friend-remove']")[0].Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-test='friend']")));
        Assert.Equal(2, friends.AntiforgeryRequests);
    }

    [Fact]
    public async Task A_failed_add_shows_an_error_and_keeps_the_list()
    {
        var friends = new FriendsServer { FailMutations = true };
        var cut = await RenderProfileAsync(friends);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-test='friend']").Count));

        cut.FindAll("[data-test='friend-remove']")[0].Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='friends-error']")));
        Assert.Equal(2, cut.FindAll("[data-test='friend']").Count);
    }

    [Fact]
    public async Task The_profile_has_no_friends_section_when_the_tenant_disables_friends()
    {
        var friends = new FriendsServer { Disabled = true };
        var cut = await RenderProfileAsync(friends);

        cut.WaitForState(() => friends.ListRequests > 0);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#profile-title")));
        Assert.Empty(cut.FindAll("[data-test='friends-section']"));
    }

    private async Task<IRenderedComponent<Profile>> RenderProfileAsync(FriendsServer friends)
    {
        var http = new ClientHttp(new HttpClient(friends) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        JSInterop.Setup<string?>("quesshi.get", _ => true).SetResult(null);
        var translator = new Translator(http);
        await translator.UseAsync("en");
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton(http);
        var auth = new AuthState(new PlayerApi(http));
        await auth.InitializeAsync();
        Services.AddSingleton(auth);
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().RegisterSlots(registry);
        Services.AddSingleton<IFormatClientRegistry>(registry);
        return Render<Profile>();
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    /// <summary>Answers the friends routes the way the server does, keeping the friend set between requests.</summary>
    private sealed class FriendsServer : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Name, string Seed, bool Online)> _players = new()
        {
            [Ada] = ("Ada", "seed-ada", true),
            [Bo] = ("Bo", "seed-bo", false),
            [Cy] = ("Cy", "seed-cy", false),
        };

        private bool _rejectNext;

        public HashSet<string> FriendIds { get; } = [Ada, Bo];
        public bool Disabled { get; init; }
        public bool FailMutations { get; init; }
        public bool RejectFirstMutation { get => _rejectNext; init => _rejectNext = value; }
        public int ListRequests { get; private set; }
        public int AntiforgeryRequests { get; private set; }
        public string? LastSearch { get; private set; }
        public List<string> CsrfHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/me") return Task.FromResult(Json($$"""{"id":"{{PlayerId}}","displayName":"North player","avatarSeed":"avatar-7","lang":"en"}"""));
            if (path.StartsWith("/i18n/", StringComparison.Ordinal)) return Task.FromResult(Json("{}"));
            if (path == "/api/antiforgery")
            {
                AntiforgeryRequests++;
                return Task.FromResult(Json($$"""{"token":"csrf"}"""));
            }

            if (!path.StartsWith("/api/x/friends", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (Disabled)
            {
                ListRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Get && path == "/api/x/friends")
            {
                ListRequests++;
                var entries = FriendIds.Select(id => new { id, displayName = _players[id].Name, avatarSeed = _players[id].Seed, isOnline = _players[id].Online }).OrderBy(e => e.displayName);
                return Task.FromResult(Json(JsonSerializer.Serialize(entries)));
            }

            if (request.Method == HttpMethod.Get && path == "/api/x/friends/search")
            {
                LastSearch = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["q"];
                var found = _players.Where(p => !FriendIds.Contains(p.Key) && p.Value.Name.Contains(LastSearch!, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new { id = p.Key, displayName = p.Value.Name, avatarSeed = p.Value.Seed });
                return Task.FromResult(Json(JsonSerializer.Serialize(found)));
            }

            CsrfHeaders.Add(request.Headers.TryGetValues("X-CSRF", out var values) ? values.Single() : "");
            if (_rejectNext)
            {
                _rejectNext = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_antiforgery"}""", Encoding.UTF8, "application/json") });
            }

            if (FailMutations) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var id = path["/api/x/friends/".Length..];
            if (request.Method == HttpMethod.Post) FriendIds.Add(id);
            else if (request.Method == HttpMethod.Delete) FriendIds.Remove(id);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
