using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Live;
using Quesshi.Web.Services;
using static Quesshi.Web.Tests.LiveScreenViews;

namespace Quesshi.Web.Tests;

public sealed class LiveScreenTests : BunitContext
{
    private static readonly Player Amir = new("amir");
    private static readonly Player Sara = new("sara");

    private readonly FakeCommands commands = new();
    private readonly Translator translator;

    public LiveScreenTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(new Tables()) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        translator = new Translator(http);
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IFormatCommands>(commands);
    }

    [Fact]
    public void A_countdown_view_renders_the_countdown_and_no_question_card()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Countdown));

        Assert.Single(cut.FindAll("[data-live-countdown]"));
        Assert.Empty(cut.FindAll("[data-live-question]"));
        Assert.Empty(cut.FindAll("[data-kind]"));
    }

    [Fact]
    public void A_question_view_renders_the_shared_ring_and_the_card_through_KindPlay()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody([])));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-kind='choice'] [data-choice]").Count));
        Assert.Single(cut.FindAll("[data-live-ring] svg.ring"));
        Assert.Equal(["Tehran", "Paris"], cut.FindAll("[data-choice]").Select(button => button.TextContent));
    }

    [Fact]
    public void A_question_view_shows_which_players_have_already_answered()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody(["sara"])));

        Assert.Equal("true", cut.Find("[data-seat='sara']").GetAttribute("data-answered"));
        Assert.Equal("false", cut.Find("[data-seat='amir']").GetAttribute("data-answered"));
    }

    [Fact]
    public void A_reveal_view_renders_KindReveal_with_every_players_answer_and_score_change()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody([])));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            Parse("amir", [Amir with { Score = 120 }, Sara with { Score = 45 }], Reveal, body: ChoiceBody([], reveal: true, mine: """{"index":0}""", responded: ["amir", "sara"]), version: 2))));

        cut.WaitForAssertion(() => Assert.Equal("Tehran", cut.Find("[data-kind='choice'] [data-correct-option]").TextContent));
        Assert.Equal("0", cut.Find("[data-kind='choice'] [data-answer-index]").GetAttribute("data-answer-index"));
        Assert.Equal("120", cut.Find("[data-reveal-player='amir'] [data-score-gain]").GetAttribute("data-score-gain"));
        Assert.Equal("45", cut.Find("[data-reveal-player='sara'] [data-score-gain]").GetAttribute("data-score-gain"));
        Assert.Equal("true", cut.Find("[data-reveal-player='sara']").GetAttribute("data-answered"));
    }

    [Fact]
    public void A_player_who_did_not_answer_is_marked_in_the_reveal()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Reveal, body: ChoiceBody([], reveal: true, responded: ["amir"])));

        Assert.Equal("false", cut.Find("[data-reveal-player='sara']").GetAttribute("data-answered"));
        Assert.Contains("live.noAnswer", cut.Find("[data-reveal-player='sara']").TextContent);
        Assert.Single(cut.FindAll("[data-live-agreement]"));
    }

    [Fact]
    public void An_eliminated_viewer_sees_the_eliminated_state()
    {
        var cut = RenderScreen(Parse("amir", [Amir with { Status = "eliminated" }, Sara], Question, body: ChoiceBody([])));

        Assert.Single(cut.FindAll("[data-live-eliminated]"));
        Assert.Empty(cut.FindAll("[data-live-left]"));
        cut.WaitForAssertion(() => Assert.All(cut.FindAll("[data-choice]"), button => Assert.True(button.HasAttribute("disabled"))));
    }

    [Fact]
    public void A_viewer_who_has_left_sees_the_left_state()
    {
        var cut = RenderScreen(Parse("amir", [Amir with { Status = "left" }, Sara], Question, body: ChoiceBody([])));

        Assert.Single(cut.FindAll("[data-live-left]"));
        Assert.Empty(cut.FindAll("[data-live-eliminated]"));
        Assert.Empty(cut.FindAll("[data-kind='choice']"));
    }

    [Fact]
    public void A_view_that_skips_from_question_to_a_later_reveal_renders_the_current_phase_and_the_previous_outcome()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, slot: 0, body: ChoiceBody([])));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            Parse("amir", [Amir with { Score = 90 }, Sara], Reveal, slot: 1, version: 5,
                body: ChoiceBody([], reveal: true, responded: ["amir"], previousResponded: ["amir", "sara"], slot: 1)))));

        cut.WaitForAssertion(() => Assert.Equal("Tehran", cut.Find("[data-kind='choice'] [data-correct-option]").TextContent));
        Assert.Equal("reveal", cut.Find("[data-live-screen]").GetAttribute("data-live-phase"));
        Assert.Equal("true", cut.Find("[data-previous-player='sara']").GetAttribute("data-answered"));
        Assert.Single(cut.FindAll("[data-previous-outcome='0']"));
    }

    [Fact]
    public void The_previous_outcome_stays_through_later_views_of_the_same_phase_and_clears_on_the_next_step()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, slot: 0, body: ChoiceBody([])));
        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            Parse("amir", [Amir, Sara], Reveal, slot: 1, version: 5,
                body: ChoiceBody([], reveal: true, responded: ["amir"], previousResponded: ["amir", "sara"], slot: 1)))));
        Assert.Single(cut.FindAll("[data-previous-outcome]"));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            Parse("amir", [Amir with { Score = 10 }, Sara], Reveal, slot: 1, version: 6,
                body: ChoiceBody([], reveal: true, responded: ["amir"], previousResponded: ["amir", "sara"], slot: 1)))));
        Assert.Single(cut.FindAll("[data-previous-outcome]"));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            Parse("amir", [Amir, Sara], Question, slot: 2, version: 7, body: ChoiceBody([], slot: 2)))));
        Assert.Empty(cut.FindAll("[data-previous-outcome]"));
    }

    [Fact]
    public void A_view_after_a_reconnect_shows_the_previous_slots_outcome_from_the_view_alone()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, slot: 2, body: ChoiceBody([], previousResponded: ["sara"], slot: 2)));

        Assert.Equal("false", cut.Find("[data-previous-player='amir']").GetAttribute("data-answered"));
        Assert.Equal("true", cut.Find("[data-previous-player='sara']").GetAttribute("data-answered"));
    }

    [Fact]
    public async Task A_hundred_player_view_renders_a_windowed_roster_not_a_hundred_rows()
    {
        await translator.UseAsync("en");
        var players = Enumerable.Range(0, 15).Select(index => new Player(index == 0 ? "amir" : $"p{index}")).ToArray();
        var cut = RenderScreen(Parse("amir", players, Question, body: ChoiceBody([]), rosterTotal: 100));

        var rows = cut.FindAll("[data-live-answered-list] li");
        Assert.Equal(15, rows.Count);
        Assert.Equal("and 85 more", cut.Find("[data-roster-more]").TextContent);
    }

    [Fact]
    public void An_opponent_answered_flag_turning_on_between_two_views_shows_the_effect_once()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody([])));
        Assert.Empty(cut.FindAll("[data-effect='opponent-answered']"));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(Parse("amir", [Amir, Sara], Question, body: ChoiceBody(["sara"]), version: 2))));
        Assert.Single(cut.FindAll("[data-effect='opponent-answered']"));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(Parse("amir", [Amir, Sara], Question, body: ChoiceBody(["sara"]), version: 3))));
        Assert.Empty(cut.FindAll("[data-effect='opponent-answered']"));
    }

    [Fact]
    public void The_viewers_own_answer_turning_on_is_not_an_opponent_answered_effect()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody([])));

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(Parse("amir", [Amir, Sara], Question, body: ChoiceBody(["amir"]), version: 2))));

        Assert.Empty(cut.FindAll("[data-effect='opponent-answered']"));
    }

    [Fact]
    public async Task Choosing_an_option_sends_the_answer_and_locks_the_card()
    {
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody([])));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-choice]").Count));

        await cut.Find("[data-choice='1']").ClickAsync();

        var sent = Assert.Single(commands.Sent);
        Assert.Equal("answer", sent.Type);
        Assert.Equal(1, ((JsonElement)sent.Payload!).GetProperty("index").GetInt32());
        cut.WaitForAssertion(() => Assert.All(cut.FindAll("[data-choice]"), button => Assert.True(button.HasAttribute("disabled"))));
    }

    [Fact]
    public async Task A_rejected_answer_keeps_the_card_open_and_says_so()
    {
        commands.Accept = false;
        var cut = RenderScreen(Parse("amir", [Amir, Sara], Question, body: ChoiceBody([])));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-choice]").Count));

        await cut.Find("[data-choice='0']").ClickAsync();

        Assert.Single(cut.FindAll("[data-live-send-failed]"));
        Assert.All(cut.FindAll("[data-choice]"), button => Assert.False(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task The_screen_renders_right_to_left_with_translated_texts_in_an_rtl_tenant_language()
    {
        await translator.UseAsync("fa");
        translator.Direction = "rtl";

        var cut = RenderScreen(Parse("amir", [Amir with { Status = "eliminated" }, Sara], Countdown));

        Assert.Equal("rtl", cut.Find("[data-live-screen]").GetAttribute("dir"));
        var persian = Persian("live.eliminated.notice");
        Assert.NotEqual("live.eliminated.notice", persian);
        Assert.Equal(persian, cut.Find("[data-live-eliminated]").TextContent.Trim());
        Assert.Equal(Persian("live.countdown.starting"), cut.Find("[data-live-countdown] p.faint").TextContent.Trim());
        Assert.Matches("^[۰-۹]+$", cut.Find("[data-live-countdown-digit]").TextContent.Trim());
    }

    private IRenderedComponent<LiveScreen> RenderScreen(SessionView view) =>
        Render<LiveScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(view)));

    private static string Persian(string key) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", "fa.json")))![key];

    private sealed class FakeCommands : IFormatCommands
    {
        public List<(string Type, object? Payload)> Sent { get; } = [];
        public bool Accept { get; set; } = true;

        public Task<bool> SendAsync(string type, object? payload = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload));
            return Task.FromResult(Accept);
        }

        public Task<bool> SendAsync(string type, object? payload, int revision, CancellationToken cancellationToken = default) =>
            SendAsync(type, payload, cancellationToken);
    }

    private sealed class Tables : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var file = path.StartsWith("/i18n/", StringComparison.Ordinal) ? Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(path)) : null;
            if (file is null || !File.Exists(file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(file), Encoding.UTF8, "application/json") });
        }
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
