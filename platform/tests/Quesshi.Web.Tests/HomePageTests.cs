using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class HomePageTests : BunitContext
{
    private const string PlayerId = "21ac7193-52fd-4d4b-9194-5cd547448072";
    private static readonly Guid Waiting = Guid.Parse("7f3c1d52-0000-4000-8000-000000000001");
    private static readonly Guid Invited = Guid.Parse("7f3c1d52-0000-4000-8000-000000000002");
    private static readonly Guid Lobby = Guid.Parse("7f3c1d52-0000-4000-8000-000000000003");

    private readonly FakeServer server = new();

    [Fact]
    public void An_enabled_registered_format_is_offered_as_a_new_session()
    {
        var cut = RenderHome(enabled: ["async_duel"], registered: ["async_duel"]);

        Assert.Equal(["async_duel"], cut.FindAll("[data-format]").Select(e => e.GetAttribute("data-format")));
    }

    [Fact]
    public void A_disabled_format_is_absent_from_the_new_session_choices()
    {
        var cut = RenderHome(enabled: ["async_duel"], registered: ["async_duel", "poll"]);

        Assert.DoesNotContain(cut.FindAll("[data-format]"), e => e.GetAttribute("data-format") == "poll");
        Assert.Single(cut.FindAll("[data-format]"));
    }

    [Fact]
    public void An_enabled_format_without_a_registered_client_is_not_offered()
    {
        var cut = RenderHome(enabled: ["async_duel", "poll"], registered: ["async_duel"]);

        Assert.DoesNotContain(cut.FindAll("[data-format]"), e => e.GetAttribute("data-format") == "poll");
    }

    [Fact]
    public async Task Creating_a_session_opens_the_created_session()
    {
        var cut = RenderHome(enabled: ["async_duel"], registered: ["async_duel"]);

        await cut.Find("[data-format='async_duel']").ClickAsync();

        var created = server.Created.Single();
        Assert.Equal("async_duel", created.GetProperty("format").GetString());
        Assert.Equal("bank", created.GetProperty("content").GetProperty("source").GetString());
        Assert.Equal($"/s/{created.GetProperty("id").GetGuid()}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public async Task A_failed_create_stays_on_home_with_an_error()
    {
        server.CreateStatus = HttpStatusCode.Conflict;
        var cut = RenderHome(enabled: ["async_duel"], registered: ["async_duel"]);

        await cut.Find("[data-format='async_duel']").ClickAsync();

        Assert.Equal("/", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
        Assert.NotNull(cut.Find("[role='alert']"));
    }

    [Fact]
    public async Task Joining_by_a_valid_code_opens_the_joined_session()
    {
        var cut = RenderHome();
        server.JoinedAfterCode = true;

        cut.Find("input[name='code']").Input("abcd");
        await cut.Find("form").SubmitAsync();

        Assert.Equal("abcd", server.JoinedCode);
        Assert.Equal($"/s/{Lobby}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public async Task Joining_with_an_unknown_code_stays_on_home_with_an_error()
    {
        var cut = RenderHome();
        server.JoinStatus = HttpStatusCode.NotFound;

        cut.Find("input[name='code']").Input("nope");
        await cut.Find("form").SubmitAsync();

        Assert.Equal("/", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
        Assert.NotNull(cut.Find("[role='alert']"));
    }

    [Fact]
    public void A_session_waiting_for_the_player_appears_in_the_inbox_section()
    {
        var cut = RenderHome();

        var card = cut.Find("[data-test='home-waiting'] [data-session]");
        Assert.Equal($"/s/{Waiting}", card.GetAttribute("href"));
        Assert.DoesNotContain(cut.FindAll("[data-session]"), e => e.GetAttribute("data-session") == Lobby.ToString());
    }

    [Fact]
    public async Task Accepting_an_invitation_opens_the_session()
    {
        var cut = RenderHome();

        await cut.Find($"[data-invitation='{Invited}'] [data-action='accept']").ClickAsync();

        Assert.Equal([$"accept:{Invited}"], server.InviteActions);
        Assert.Equal($"/s/{Invited}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public async Task Declining_an_invitation_removes_its_card_without_accepting()
    {
        var cut = RenderHome();

        await cut.Find($"[data-invitation='{Invited}'] [data-action='decline']").ClickAsync();

        Assert.Equal([$"decline:{Invited}"], server.InviteActions);
        Assert.Empty(cut.FindAll($"[data-invitation='{Invited}']"));
        Assert.Equal("/", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public async Task A_declined_invitation_that_fails_keeps_its_card()
    {
        server.DeclineStatus = HttpStatusCode.NotFound;
        var cut = RenderHome();

        await cut.Find($"[data-invitation='{Invited}'] [data-action='decline']").ClickAsync();

        Assert.Single(cut.FindAll($"[data-invitation='{Invited}']"));
        Assert.NotNull(cut.Find("[role='alert']"));
    }

    [Fact]
    public void Home_tile_contributions_render_with_the_enabled_formats()
    {
        var cut = RenderHome(enabled: ["async_duel"], registered: ["async_duel"], tile: true);

        Assert.Equal("tile:async_duel", cut.Find("[data-test='home-tile']").TextContent);
    }

    private IRenderedComponent<Home> RenderHome(string[]? enabled = null, string[]? registered = null, bool tile = false)
    {
        enabled ??= [];
        server.Enabled = enabled;
        var http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, JSInterop.JSRuntime);
        config.LoadAsync().GetAwaiter().GetResult();
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        var api = new PlayerApi(http);
        var auth = new AuthState(api);
        auth.InitializeAsync().GetAwaiter().GetResult();
        Services.AddSingleton(config);
        Services.AddSingleton(translator);
        Services.AddSingleton(auth);
        Services.AddSingleton(api);
        Services.AddSingleton(new PlatformState(config));
        var registry = new ClientRegistry(new NoLoader());
        foreach (var id in registered ?? enabled) registry.RegisterFormat(new FakeFormat(id));
        if (tile) registry.RegisterSlot(ClientSlots.HomeTile, typeof(TileContribution));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        return Render<Home>();
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class Screen : ComponentBase { }

    private sealed class FakeFormat(string id) : IFormatClient
    {
        public string Id => id;
        public Type Settings => typeof(Screen);
        public Type Screen => typeof(Home);
        public Type Result => typeof(Home);
    }

    private sealed class TileContribution : ComponentBase
    {
        [Parameter] public HomeTileParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "span"); b.AddAttribute(1, "data-test", "home-tile"); b.AddContent(2, $"tile:{string.Join(",", Parameters.EnabledFormats)}"); b.CloseElement(); }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public string[] Enabled { get; set; } = [];
        public List<JsonElement> Created { get; } = [];
        public List<string> InviteActions { get; } = [];
        public HttpStatusCode CreateStatus { get; set; } = HttpStatusCode.Created;
        public HttpStatusCode JoinStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode DeclineStatus { get; set; } = HttpStatusCode.OK;
        public bool JoinedAfterCode { get; set; }
        public string? JoinedCode { get; private set; }
        private bool joined;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/api/config":
                    return Json($$"""{"name":"Tenant","languages":[{"code":"en","displayName":"English","direction":"ltr"}],"defaultLanguage":"en","capabilities":{"format":{{JsonSerializer.Serialize(Enabled)}},"source":["bank"]},"signInMethods":[],"configVersion":1,"serverBuildId":"test"}""");
                case "/api/me":
                    return Json($$"""{"id":"{{PlayerId}}","displayName":"North player","avatarSeed":"avatar-7","lang":"en"}""");
                case "/api/antiforgery":
                    return Json("""{"token":"csrf"}""");
                case "/api/me/inbox":
                    var lobbies = joined ? $$"""[{"id":"{{Lobby}}","code":"ABCD","format":"async_duel"}]""" : "[]";
                    return Json($$"""{"version":1,"invitations":[{"sessionId":"{{Invited}}","invitedBy":"{{Guid.NewGuid()}}","expiresAt":"2030-01-01T00:00:00Z"}],"lobbies":{{lobbies}},"turns":[{"id":"{{Waiting}}","code":"WXYZ","format":"async_duel"}],"items":[]}""");
                case "/api/sessions":
                    if (CreateStatus != HttpStatusCode.Created) return Json("""{"error":"format_unavailable"}""", CreateStatus);
                    Created.Add(JsonDocument.Parse(body).RootElement.Clone());
                    return Json("""{"id":"00000000-0000-0000-0000-000000000000","code":"ABCD"}""", HttpStatusCode.Created);
                case "/api/sessions/join":
                    if (JoinStatus != HttpStatusCode.OK) return Json("""{"error":"not_found"}""", JoinStatus);
                    JoinedCode = JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();
                    joined = JoinedAfterCode;
                    return Json("""{"code":"joined","version":2}""");
            }

            if (path.EndsWith("/invites/accept", StringComparison.Ordinal))
            {
                InviteActions.Add($"accept:{path.Split('/')[3]}");
                return Json("""{"code":"joined","version":2}""");
            }

            if (path.EndsWith("/invites/decline", StringComparison.Ordinal))
            {
                InviteActions.Add($"decline:{path.Split('/')[3]}");
                return DeclineStatus == HttpStatusCode.OK ? Json("""{"code":"declined"}""") : Json("""{"error":"invite_not_found"}""", DeclineStatus);
            }

            return path.StartsWith("/i18n/", StringComparison.Ordinal) ? Json("{}") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
