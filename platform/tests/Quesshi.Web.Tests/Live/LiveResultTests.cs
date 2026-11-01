using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Live;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests.Live;

public sealed class LiveResultTests : BunitContext
{
    private const string Lobby = "0f8a2c64-77a5-4d4e-8c1f-2d2a4a1e9b01";
    private readonly Recorder recorder = new();
    private readonly ClientRegistry registry = new(new NoLoader());
    private readonly PlatformState state;
    private readonly ClientHttp http;
    private readonly LanguageSelection language;
    private readonly AuthState auth;

    public LiveResultTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        http = new ClientHttp(new HttpClient(recorder) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, null!);
        var translator = new Translator(http);
        auth = new AuthState(new PlayerApi(http));
        language = new LanguageSelection(config, translator, JSInterop.JSRuntime);
        state = new PlatformState(config);
        new WebPluginsEntry().RegisterFormats(registry);
        Services.AddSingleton(http);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IClientViewer>(new ClientViewer(language, config, translator, auth));
        Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
        Services.AddSingleton(new LobbyInviteCandidates(new PlayerApi(http), config));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        Services.AddSingleton(state);
        config.LoadAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task The_result_lists_every_player_by_place_with_the_place_shown()
    {
        await SignInAsync(guest: false);

        var cut = RenderResult(Seats(("sara", 1, 2, "loss", "done"), ("amir", 0, 1, "win", "done"), ("reza", 2, 3, "loss", "done")));

        Assert.Equal(["amir", "sara", "reza"], cut.FindAll("[data-standing]").Select(row => row.GetAttribute("data-standing")));
        Assert.Equal(["1st", "2nd", "3rd"], cut.FindAll("[data-standing-place]").Select(place => place.TextContent));
    }

    [Fact]
    public async Task The_result_marks_a_player_who_abandoned()
    {
        await SignInAsync(guest: false);

        var cut = RenderResult(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "eliminated")));

        Assert.Empty(cut.Find("[data-standing='amir']").QuerySelectorAll("[data-standing-abandoned]"));
        Assert.Single(cut.Find("[data-standing='sara']").QuerySelectorAll("[data-standing-abandoned]"));
    }

    [Fact]
    public async Task The_result_shows_the_rematch_button_to_a_signed_in_player()
    {
        await SignInAsync(guest: false);

        var cut = RenderResult(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "done")));

        Assert.Single(cut.FindAll("[data-live-rematch]"));
    }

    [Fact]
    public async Task The_result_hides_the_rematch_button_from_a_guest()
    {
        await SignInAsync(guest: true);

        var cut = RenderResult(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "done")));

        Assert.Empty(cut.FindAll("[data-live-rematch]"));
        Assert.Single(cut.FindAll("[data-standing='amir']"));
    }

    [Fact]
    public async Task Activating_rematch_sends_the_command_and_navigates_to_the_lobby_the_server_returns()
    {
        await SignInAsync(guest: false);
        var cut = RenderResult(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "done")));

        cut.Find("[data-live-rematch]").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/s/{Lobby}", Services.GetRequiredService<NavigationManager>().Uri));
        Assert.Contains($"POST /api/sessions/{LobbyViews.SessionId}/rematch", recorder.Mutations);
    }

    [Fact]
    public async Task A_refused_rematch_shows_the_failure_and_stays_on_the_result()
    {
        await SignInAsync(guest: false);
        recorder.RematchStatus = HttpStatusCode.Conflict;
        var cut = RenderResult(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "done")));

        cut.Find("[data-live-rematch]").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-live-rematch-error]")));
        Assert.DoesNotContain(Lobby, Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task The_session_page_renders_the_live_result_with_the_session_result_slot_contributions()
    {
        await SignInAsync(guest: false);
        registry.RegisterSlot(ClientSlots.SessionResult, typeof(Contribution));
        state.AcceptView(PlatformState.ParseView(ViewJson(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "done"))))!);

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Single(cut.FindAll("[data-live-result]"));
        Assert.Equal("slot:" + LobbyViews.SessionId, cut.Find("[data-result-contribution]").TextContent);
    }

    [Fact]
    public async Task In_an_rtl_tenant_language_the_result_is_rtl_with_translated_texts()
    {
        await SignInAsync(guest: false);
        await language.UseAsync("fa");
        var fa = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", "fa.json")))!;

        var cut = RenderResult(Seats(("amir", 0, 1, "win", "done"), ("sara", 1, 2, "loss", "left")));

        Assert.Equal("rtl", cut.Find("[data-live-result]").GetAttribute("dir"));
        Assert.Equal(fa["live.rematch.button"], cut.Find("[data-live-rematch]").TextContent);
        Assert.Equal(fa["result.standings"], cut.Find("h2").TextContent);
        Assert.Equal(fa["result.abandoned"], cut.Find("[data-standing-abandoned]").TextContent);
        Assert.Equal("۱م", cut.FindAll("[data-standing-place]")[0].TextContent);
    }

    private async Task SignInAsync(bool guest)
    {
        recorder.Guest = guest;
        await auth.InitializeAsync();
        await language.InitializeAsync();
    }

    private IRenderedComponent<LiveResult> RenderResult(string seatsJson) =>
        Render<LiveResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(PlatformState.ParseView(ViewJson(seatsJson))!)));

    private static string Seats(params (string Id, int Seat, int? Place, string Outcome, string Status)[] seats) => string.Join(",", seats.Select(s =>
        $$"""{"id":"{{s.Id}}","name":"{{s.Id}}","seat":{{s.Seat}},"role":"player","status":"{{s.Status}}","score":{{100 - s.Seat}},"place":{{s.Place?.ToString() ?? "null"}},"outcome":"{{s.Outcome}}","online":false}"""));

    private static string ViewJson(string seats) =>
        $$$"""{"id":"{{{LobbyViews.SessionId}}}","code":"ABCD23","format":"live","status":"finished","phase":"done","slot":-1,"step":0,"version":3,"configVersion":1,"now":"2026-01-01T00:00:00Z","deadline":null,"me":{"id":"amir","seat":0,"role":"player","status":"done","score":100,"place":1,"outcome":"win"},"roster":{"total":2,"seats":[{{{seats}}}]},"content":{},"rules":{},"body":{}}""";

    private sealed class Contribution : ComponentBase
    {
        [Parameter] public SessionResultParameters Parameters { get; set; } = default!;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "data-result-contribution", "");
            builder.AddContent(2, "slot:" + Parameters.Session.Id);
            builder.CloseElement();
        }
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Mutations { get; } = [];
        public bool Guest { get; set; }
        public HttpStatusCode RematchStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(path switch
                {
                    "/api/antiforgery" => Json("""{"token":"csrf"}"""),
                    "/api/me" => Json($$"""{"id":"6b0d5a0e-1d2c-4a52-9a43-0f3a7e1c9b99","displayName":"Amir","avatarSeed":"a","lang":"en","isGuest":{{(Guest ? "true" : "false")}}}"""),
                    "/api/config" => Json("""{"name":"t","languages":[{"code":"en","displayName":"English","direction":"ltr","appName":null},{"code":"fa","displayName":"Persian","direction":"rtl","appName":null}],"defaultLanguage":"en","capabilities":{},"configVersion":1,"serverBuildId":"x"}"""),
                    _ when path.StartsWith("/i18n/", StringComparison.Ordinal) => Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(path)))),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                });
            Mutations.Add($"{request.Method} {path}");
            return Task.FromResult(RematchStatus == HttpStatusCode.OK
                ? Json($$"""{"id":"{{Lobby}}","code":"rematch"}""")
                : Json("""{"error":"rematch_not_offered"}""", RematchStatus));
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }
}
