using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Pages;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class JoinPageTests : BunitContext
{
    private static readonly Guid Lobby = Guid.Parse("7f3c1d52-0000-4000-8000-000000000003");

    private readonly FakeServer server = new();

    [Fact]
    public async Task Pressing_join_before_the_app_was_ready_makes_one_join_call_and_opens_the_lobby()
    {
        var cut = await RenderJoinAsync(pressedBeforeReady: true);

        Assert.Equal(["ABCD"], server.Joined);
        Assert.Equal($"/s/{Lobby}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
        Assert.Empty(cut.FindAll("[data-test=join-error]"));
    }

    [Fact]
    public async Task Without_a_press_nothing_is_sent_until_the_player_joins_by_hand()
    {
        var cut = await RenderJoinAsync(pressedBeforeReady: false);
        Assert.Empty(server.Joined);

        await cut.Find("[data-test=join-submit]").ClickAsync();

        Assert.Equal(["ABCD"], server.Joined);
        Assert.Equal($"/s/{Lobby}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public async Task A_second_screen_for_the_same_press_does_not_join_again()
    {
        await RenderJoinAsync(pressedBeforeReady: true);

        Render<Join>(p => p.Add(c => c.Code, "ABCD"));

        Assert.Equal(["ABCD"], server.Joined);
    }

    [Fact]
    public async Task An_expired_code_shows_the_expired_outcome_and_does_not_open_a_session()
    {
        server.JoinError = "expired";

        var cut = await RenderJoinAsync(pressedBeforeReady: true);

        Assert.Equal("challenge.lobbyExpired", cut.Find("[data-test=join-error]").TextContent.Trim());
        Assert.DoesNotContain("/s/", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(["ABCD"], server.Joined);
    }

    private async Task<IRenderedComponent<Join>> RenderJoinAsync(bool pressedBeforeReady)
    {
        JSInterop.Setup<bool>("quesshi.takeover.claim").SetResult(pressedBeforeReady);
        var http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, JSInterop.JSRuntime);
        await config.LoadAsync();
        var translator = new Translator(http);
        await translator.UseAsync("en");
        var api = new PlayerApi(http);
        var takeover = new InviteTakeover(JSInterop.JSRuntime);
        await takeover.ReadyAsync();
        Services.AddSingleton(config);
        Services.AddSingleton(translator);
        Services.AddSingleton(new AuthState(api));
        Services.AddSingleton(api);
        Services.AddSingleton(new PlatformState(config));
        Services.AddSingleton(takeover);
        return Render<Join>(p => p.Add(c => c.Code, "ABCD"));
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<string> Joined { get; } = [];
        public string? JoinError { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            switch (path)
            {
                case "/api/config":
                    return Json("""{"name":"Tenant","languages":[{"code":"en","displayName":"English","direction":"ltr"}],"defaultLanguage":"en","capabilities":{"format":[],"source":["bank"],"feature":[]},"signInMethods":[],"configVersion":1,"serverBuildId":"test"}""");
                case "/api/antiforgery":
                    return Json("""{"token":"csrf"}""");
                case "/api/me/inbox":
                    return Json($$"""{"version":1,"invitations":[],"lobbies":[{"id":"{{Lobby}}","code":"ABCD","format":"async_duel"}],"turns":[],"items":[]}""");
                case "/api/sessions/join":
                    Joined.Add(JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!);
                    return JoinError is null
                        ? Json("""{"code":"joined","version":2}""")
                        : Json($$"""{"error":"{{JoinError}}"}""", HttpStatusCode.Conflict);
            }

            return path.StartsWith("/i18n/", StringComparison.Ordinal) ? Json("{}") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
