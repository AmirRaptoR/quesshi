using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Domain;
using Quesshi.Formats.Live;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Live;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class LiveClientTests : BunitContext
{
    private readonly Recorder recorder = new();
    private readonly ClientRegistry registry = new(new NoLoader());
    private readonly PlatformState state;

    public LiveClientTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(recorder) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        state = new PlatformState(new AppConfig(http, null!));
        new WebPluginsEntry().RegisterFormats(registry);
        Services.AddSingleton(http);
        Services.AddSingleton(new Translator(http));
        Services.AddSingleton<IClientTranslator>(sp => sp.GetRequiredService<Translator>());
        Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
        Services.AddSingleton(new LobbyInviteCandidates(new PlayerApi(http), new AppConfig(http, null!)));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        Services.AddSingleton(state);
    }

    [Fact]
    public void The_registry_resolves_live_to_a_client_with_component_types()
    {
        var client = registry.ResolveFormat("live");

        Assert.IsType<LiveClient>(client);
        Assert.All(new[] { client!.Settings, client.Screen, client.Result }, type => Assert.True(typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(type)));
    }

    [Fact]
    public void The_settings_picker_renders_question_count_and_language_options()
    {
        var cut = RenderSettings(owner: true);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-live-language]").Count));
        Assert.Equal(MatchRules.QuestionCountChoices.Length, cut.FindAll("[data-live-count]").Count);
    }

    [Fact]
    public void The_capacity_choice_is_capped_at_the_format_maximum()
    {
        var cut = RenderSettings(owner: true);
        var input = cut.Find("[data-live-capacity]");

        Assert.Equal(LiveFormat.MeasuredMaxPlayers.ToString(), input.GetAttribute("max"));
        input.Change(LiveFormat.MeasuredMaxPlayers + 1);

        Assert.Single(cut.FindAll("[data-live-capacity-error]"));
        Assert.DoesNotContain(recorder.Mutations, m => m.StartsWith("PUT"));
    }

    [Fact]
    public void A_capacity_below_the_minimum_participants_is_rejected_without_a_request()
    {
        var cut = RenderSettings(owner: true);

        cut.Find("[data-live-capacity]").Change(MatchRules.MinParticipants - 1);

        Assert.Single(cut.FindAll("[data-live-capacity-error]"));
        Assert.Empty(recorder.Mutations);
    }

    [Fact]
    public void A_valid_capacity_is_sent_to_the_settings_endpoint()
    {
        var cut = RenderSettings(owner: true);

        cut.Find("[data-live-capacity]").Change(MatchRules.MinParticipants);

        cut.WaitForAssertion(() => Assert.Contains("PUT /api/sessions/6b0d5a0e-1d2c-4a52-9a43-0f3a7e1c9b11/settings", recorder.Mutations));
        Assert.Empty(cut.FindAll("[data-live-capacity-error]"));
    }

    [Fact]
    public void A_non_owner_sees_the_controls_inert()
    {
        var cut = RenderSettings(owner: false);

        Assert.True(cut.Find("[data-live-capacity]").HasAttribute("disabled"));
    }

    [Fact]
    public void The_session_page_renders_the_live_screen_for_a_running_live_session()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "running", format: "live"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Single(cut.FindAll("[data-live-screen]"));
    }

    private IRenderedComponent<LiveSettings> RenderSettings(bool owner) =>
        Render<LiveSettings>(p => p.Add(c => c.Parameters,
            new FormatSettingsParameters(LobbyViews.Parse(owner ? "amir" : "sara", ["amir", "sara"], format: "live"), owner)));

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Mutations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(path switch
                {
                    "/api/antiforgery" => Json("""{"token":"csrf"}"""),
                    "/api/config" => Json("""{"name":"t","languages":[{"code":"en","displayName":"English","direction":"ltr","appName":null},{"code":"fa","displayName":"Persian","direction":"rtl","appName":null}],"defaultLanguage":"en","capabilities":{},"configVersion":1,"serverBuildId":"x"}"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                });
            Mutations.Add($"{request.Method} {path}");
            return Task.FromResult(Json("""{"code":"configured"}"""));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
