using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Domain;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Poll;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PollClientTests : BunitContext
{
    private readonly Server server = new();
    private readonly ClientRegistry registry = new(new NoLoader());
    private readonly ClientHttp http;
    private readonly PlatformState state;
    private readonly Translator translator;

    public PollClientTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, JSInterop.JSRuntime);
        state = new PlatformState(config);
        translator = new Translator(http);
        new WebPluginsEntry().RegisterFormats(registry);
        Services.AddSingleton(http);
        Services.AddSingleton(config);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
        Services.AddSingleton(new LobbyInviteCandidates(new PlayerApi(http), config));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        Services.AddSingleton(state);
    }

    [Fact]
    public void The_registry_resolves_poll_to_a_client_with_component_types()
    {
        var client = registry.ResolveFormat("poll");

        Assert.IsType<PollClient>(client);
        Assert.All(new[] { client!.Settings, client.Screen, client.Result }, type => Assert.True(typeof(IComponent).IsAssignableFrom(type)));
    }

    [Fact]
    public void The_registered_screen_and_result_components_accept_the_format_parameters()
    {
        var client = registry.ResolveFormat("poll")!;
        var session = LobbyViews.Parse("amir", ["amir", "sara"], status: "finished", format: "poll");

        var screen = Render<DynamicComponent>(p => p.Add(c => c.Type, client.Screen)
            .Add(c => c.Parameters, new Dictionary<string, object> { ["Parameters"] = new FormatScreenParameters(session) }));
        var result = Render<DynamicComponent>(p => p.Add(c => c.Type, client.Result)
            .Add(c => c.Parameters, new Dictionary<string, object> { ["Parameters"] = new FormatScreenParameters(session) }));
        var settings = Render<DynamicComponent>(p => p.Add(c => c.Type, client.Settings)
            .Add(c => c.Parameters, new Dictionary<string, object> { ["Parameters"] = new FormatSettingsParameters(session, true) }));

        Assert.Single(screen.FindAll("[data-poll-screen]"));
        Assert.Single(result.FindAll("[data-poll-result]"));
        Assert.NotEmpty(settings.FindAll("[data-poll-count]"));
    }

    [Fact]
    public async Task An_enabled_poll_format_is_offered_for_new_play()
    {
        server.Enabled = ["poll"];
        var config = new AppConfig(http, JSInterop.JSRuntime);
        await config.LoadAsync();

        var offered = new HomeSessions(new PlayerApi(http), state, config).EnabledFormats(id => registry.ResolveFormat(id) is not null);

        Assert.Equal(["poll"], offered);
    }

    [Fact]
    public async Task A_running_poll_session_still_renders_after_the_tenant_turns_the_format_off()
    {
        server.Enabled = [];
        var config = new AppConfig(http, JSInterop.JSRuntime);
        await config.LoadAsync();
        Assert.Empty(new HomeSessions(new PlayerApi(http), state, config).EnabledFormats(id => registry.ResolveFormat(id) is not null));
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "running", format: "poll"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Single(cut.FindAll("[data-poll-screen]"));
    }

    [Fact]
    public void The_settings_picker_offers_exactly_the_shared_question_counts()
    {
        var cut = RenderSettings(owner: true);

        Assert.Equal(["10", "20", "30", "40", "50", "100"], cut.FindAll("[data-poll-count]").Select(e => e.GetAttribute("data-poll-count")));
    }

    [Fact]
    public void The_settings_picker_offers_only_poll_family_categories_in_the_selected_language()
    {
        var cut = RenderSettings(owner: true);

        cut.WaitForAssertion(() => Assert.Equal(["Food", "Travel"], cut.FindAll("[data-poll-category] .topic-tile__name").Select(e => e.TextContent.Trim())));
        cut.Find("[data-poll-language='fa']").Click();

        cut.WaitForAssertion(() => Assert.Equal(["غذا"], cut.FindAll("[data-poll-category] .topic-tile__name").Select(e => e.TextContent.Trim())));
        Assert.All(server.CategoryQueries, query => Assert.Contains("family=poll", query));
        Assert.Contains(server.CategoryQueries, query => query.Contains("language=fa"));
    }

    [Fact]
    public void A_capacity_from_two_to_eight_is_accepted()
    {
        var cut = RenderSettings(owner: true);

        cut.Find("[data-poll-capacity]").Change(8);

        cut.WaitForAssertion(() => Assert.Equal(8, server.Settings.Last().GetProperty("capacity").GetInt32()));
        Assert.Empty(cut.FindAll("[data-poll-error]"));
        Assert.Equal("8", cut.Find("[data-poll-capacity]").GetAttribute("max"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void A_capacity_outside_two_to_eight_is_rejected_without_a_request(int capacity)
    {
        var cut = RenderSettings(owner: true);

        cut.Find("[data-poll-capacity]").Change(capacity);

        Assert.Single(cut.FindAll("[data-poll-error]"));
        Assert.Empty(server.Settings);
    }

    [Fact]
    public void Choosing_settings_sends_count_categories_and_capacity_to_the_settings_endpoint()
    {
        var cut = RenderSettings(owner: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-poll-category]")));

        cut.Find("[data-poll-count='30']").Click();
        cut.WaitForAssertion(() => Assert.Single(server.Settings));
        cut.Find("[data-poll-category='11111111-1111-1111-1111-111111111111']").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, server.Settings.Count));
        var sent = server.Settings.Last();
        Assert.Equal(30, sent.GetProperty("content").GetProperty("count").GetInt32());
        Assert.Equal(["11111111-1111-1111-1111-111111111111"],
            sent.GetProperty("content").GetProperty("parameters").GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_refused_settings_change_shows_an_error_and_keeps_the_previous_choice()
    {
        await translator.UseAsync("en");
        server.SettingsStatus = HttpStatusCode.BadRequest;
        var cut = RenderSettings(owner: true);
        var before = cut.FindAll("[data-poll-count].is-on").Select(e => e.GetAttribute("data-poll-count")).ToArray();

        cut.Find("[data-poll-count='50']").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-poll-error]")));
        Assert.Equal(before, cut.FindAll("[data-poll-count].is-on").Select(e => e.GetAttribute("data-poll-count")));
        Assert.NotEqual("common.error", cut.Find("[data-poll-error]").TextContent);
    }

    [Theory]
    [InlineData("en", "ltr", "Players")]
    [InlineData("fa", "rtl", "بازیکنان")]
    [InlineData("nl", "ltr", "Spelers")]
    public async Task The_settings_picker_is_translated_and_directional_without_raw_keys(string language, string direction, string players)
    {
        server.DefaultLanguage = language;
        await translator.UseAsync(language);

        var cut = RenderSettings(owner: true);

        cut.WaitForAssertion(() => Assert.Equal(direction, cut.Find("[data-poll-settings]").GetAttribute("dir")));
        Assert.Contains(players, cut.Markup);
        Assert.DoesNotMatch(@"\b(home|lang|lobby|voting|common)\.[a-zA-Z]+", cut.Markup);
    }

    private IRenderedComponent<PollSettings> RenderSettings(bool owner) =>
        Render<PollSettings>(p => p.Add(c => c.Parameters,
            new FormatSettingsParameters(LobbyViews.Parse(owner ? "amir" : "sara", ["amir", "sara"], format: "poll"), owner)));

    private sealed class Server : HttpMessageHandler
    {
        public string[] Enabled { get; set; } = [];
        public string DefaultLanguage { get; set; } = "en";
        public HttpStatusCode SettingsStatus { get; set; } = HttpStatusCode.OK;
        public List<JsonElement> Settings { get; } = [];
        public List<string> CategoryQueries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (SettingsStatus != HttpStatusCode.OK) return Json("""{"error":"invalid_content"}""", SettingsStatus);
                Settings.Add(JsonDocument.Parse(body).RootElement.Clone());
                return Json("""{"code":"configured"}""");
            }

            if (path.StartsWith("/i18n/", StringComparison.Ordinal))
            {
                var file = Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(path));
                return Json(await File.ReadAllTextAsync(file, cancellationToken));
            }

            switch (path)
            {
                case "/api/config":
                    return Json($$"""{"name":"t","languages":[{"code":"en","displayName":"English","direction":"ltr"},{"code":"fa","displayName":"Persian","direction":"rtl"},{"code":"nl","displayName":"Dutch","direction":"ltr"}],"defaultLanguage":"{{DefaultLanguage}}","capabilities":{"format":{{JsonSerializer.Serialize(Enabled)}},"source":["bank"]},"configVersion":1,"serverBuildId":"x"}""");
                case "/api/antiforgery":
                    return Json("""{"token":"csrf"}""");
                case "/api/categories":
                    CategoryQueries.Add(request.RequestUri.Query);
                    return Json(request.RequestUri.Query.Contains("language=fa")
                        ? """[{"id":"11111111-1111-1111-1111-111111111111","slug":"food","name":"غذا","icon":"F","color":"#fff","sortOrder":1}]"""
                        : """[{"id":"11111111-1111-1111-1111-111111111111","slug":"food","name":"Food","icon":"F","color":"#fff","sortOrder":1},{"id":"22222222-2222-2222-2222-222222222222","slug":"travel","name":"Travel","icon":"T","color":"#fff","sortOrder":2}]""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
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
