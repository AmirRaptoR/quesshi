using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.AsyncDuel;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AsyncDuelScreenTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeCommands commands = new();
    private readonly Translator translator;

    public AsyncDuelScreenTests()
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
    public async Task Idle_shows_a_start_button_that_sends_next()
    {
        var cut = RenderScreen(View("idle"));

        await cut.Find("[data-duel-start]").ClickAsync();

        Assert.Equal("next", Assert.Single(commands.Sent).Type);
        Assert.Empty(cut.FindAll("[data-duel-question]"));
    }

    [Fact]
    public async Task Answered_shows_a_next_button_that_sends_next()
    {
        var cut = RenderScreen(View("1", body: Body([Answer(credit: 1, points: 80)], true)));

        await cut.Find("[data-duel-next]").ClickAsync();

        Assert.Equal("next", Assert.Single(commands.Sent).Type);
    }

    [Fact]
    public void Question_renders_the_card_through_KindPlay_with_a_twenty_second_ring()
    {
        var cut = RenderScreen(View("0", deadline: Now.AddSeconds(20), body: Body()));

        cut.WaitForAssertion(() => Assert.Equal(["Tehran", "Paris"], cut.FindAll("[data-kind='choice'] [data-choice]").Select(b => b.TextContent)));
        Assert.Single(cut.FindAll("[data-duel-ring] svg.ring"));
        Assert.DoesNotContain("warn", cut.Find("[data-duel-ring] .ring__fill").ClassName);
        Assert.Equal("1 / 3", cut.Find("[data-duel-slot]").TextContent.Trim());
    }

    [Fact]
    public async Task Choosing_an_option_sends_the_answer_and_locks_the_card()
    {
        var cut = RenderScreen(View("0", body: Body()));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-choice]").Count));

        await cut.Find("[data-choice='1']").ClickAsync();

        var sent = Assert.Single(commands.Sent);
        Assert.Equal("answer", sent.Type);
        Assert.Equal(1, ((JsonElement)sent.Payload!).GetProperty("index").GetInt32());
        cut.WaitForAssertion(() => Assert.All(cut.FindAll("[data-choice]"), b => Assert.True(b.HasAttribute("disabled"))));
    }

    [Fact]
    public void After_answering_the_outcome_and_the_reveal_show_through_KindReveal()
    {
        var cut = RenderScreen(View("1", body: Body([Answer(credit: 1, points: 80, stored: """{"index":0}""")], true)));

        cut.WaitForAssertion(() => Assert.Equal("Tehran", cut.Find("[data-kind='choice'] [data-correct-option]").TextContent));
        Assert.Equal("correct", cut.Find("[data-duel-outcome]").GetAttribute("data-duel-outcome"));
        Assert.Equal("+80", cut.Find("[data-duel-points]").TextContent.Trim());
        Assert.Equal("0", cut.Find("[data-answer-index]").GetAttribute("data-answer-index"));
    }

    [Fact]
    public void A_wrong_answer_is_reported_as_wrong()
    {
        var cut = RenderScreen(View("1", body: Body([Answer(credit: 0, points: 0, stored: """{"index":1}""")], true)));

        Assert.Equal("wrong", cut.Find("[data-duel-outcome]").GetAttribute("data-duel-outcome"));
    }

    [Fact]
    public async Task When_the_clock_runs_out_the_screen_shows_a_miss_and_offers_next()
    {
        var cut = RenderScreen(View("0", deadline: Now.AddSeconds(-3), body: Body()));

        Assert.Single(cut.FindAll("[data-duel-timeup]"));
        cut.WaitForAssertion(() => Assert.All(cut.FindAll("[data-choice]"), b => Assert.True(b.HasAttribute("disabled"))));
        await cut.Find("[data-duel-next]").ClickAsync();
        Assert.Equal("next", Assert.Single(commands.Sent).Type);

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            View("1", body: Body([Answer(credit: 0, points: 0, miss: true)], true), version: 2))));
        Assert.Equal("missed", cut.Find("[data-duel-outcome]").GetAttribute("data-duel-outcome"));
        Assert.Single(cut.FindAll("[data-duel-next]"));
    }

    [Fact]
    public async Task The_time_up_next_button_stays_disabled_until_the_server_grace_has_passed()
    {
        var cut = RenderScreen(View("0", deadline: Now.AddSeconds(-1), body: Body()));

        Assert.Single(cut.FindAll("[data-duel-timeup]"));
        Assert.True(cut.Find("[data-duel-next]").HasAttribute("disabled"));
        await cut.Find("[data-duel-next]").ClickAsync();
        Assert.Empty(commands.Sent);
    }

    [Fact]
    public void Reloading_mid_question_draws_the_remaining_time_from_the_deadline_not_a_fresh_twenty_seconds()
    {
        var cut = RenderScreen(View("0", deadline: Now.AddSeconds(4), body: Body()));

        var fill = cut.Find("[data-duel-ring] .ring__fill");
        Assert.Contains("hot", fill.ClassName);
        var circumference = 2 * Math.PI * 19;
        var offset = double.Parse(fill.GetAttribute("stroke-dashoffset")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(circumference * (1 - 4.0 / 20), offset, 3);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-choice]").Count));
    }

    [Fact]
    public void The_rendered_screen_carries_no_opponent_answers_or_scores_while_the_viewer_is_mid_run()
    {
        var body = Body([Answer(credit: 1, points: 80)], true);
        body["Others"] = new JsonArray(new JsonObject
        {
            ["Player"] = "sara", ["Score"] = 777, ["Answers"] = new JsonArray(new JsonObject { ["Points"] = 555 }),
        });

        foreach (var phase in new[] { "0", "1" })
        {
            var cut = RenderScreen(View(phase, body: body));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-kind='choice']")));
            Assert.DoesNotContain("777", cut.Markup);
            Assert.DoesNotContain("555", cut.Markup);
            Assert.DoesNotContain("SARA", cut.Markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Done_while_others_still_play_shows_the_waiting_state()
    {
        var cut = RenderScreen(View("2", body: Body([Answer(credit: 1, points: 80)], true)));

        Assert.Single(cut.FindAll("[data-duel-waiting]"));
        Assert.Empty(cut.FindAll("[data-duel-next]"));
        Assert.Empty(cut.FindAll("[data-duel-question]"));
    }

    [Fact]
    public void The_correct_and_wrong_cues_play_when_the_answer_lands_and_not_on_a_reload()
    {
        JSInterop.Setup<string?>("quesshi.get", "quesshi.sounds").SetResult("1");
        JSInterop.Setup<bool>("quesshi.sounds.armed").SetResult(true);
        JSInterop.SetupVoid("quesshi.sounds.play", _ => true);

        var reloaded = RenderScreen(View("1", body: Body([Answer(credit: 1, points: 80)], true)));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "quesshi.sounds.play");

        var cut = RenderScreen(View("0", body: Body()));
        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            View("1", body: Body([Answer(credit: 1, points: 80)], true), version: 2))));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "quesshi.sounds.play" && (string?)i.Arguments[0] == "correct"));

        var wrong = RenderScreen(View("0", body: Body()));
        wrong.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(
            View("1", body: Body([Answer(credit: 0, points: 0)], true), version: 2))));
        wrong.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "quesshi.sounds.play" && (string?)i.Arguments[0] == "wrong"));
        Assert.NotNull(reloaded);
    }

    [Fact]
    public void The_tick_cue_plays_once_in_the_last_three_seconds()
    {
        JSInterop.Setup<string?>("quesshi.get", "quesshi.sounds").SetResult("1");
        JSInterop.Setup<bool>("quesshi.sounds.armed").SetResult(true);
        JSInterop.SetupVoid("quesshi.sounds.play", _ => true);

        var cut = RenderScreen(View("0", deadline: Now.AddSeconds(3), body: Body()));

        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "quesshi.sounds.play" && (string?)i.Arguments[0] == "tick"));
        Thread.Sleep(300);
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "quesshi.sounds.play");
    }

    [Fact]
    public async Task The_screen_renders_right_to_left_with_translated_texts_in_an_rtl_tenant_language()
    {
        await translator.UseAsync("fa");
        translator.Direction = "rtl";

        var cut = RenderScreen(View("2", body: Body()));

        Assert.Equal("rtl", cut.Find("[data-duel-screen]").GetAttribute("dir"));
        var persian = Persian("home.waiting");
        Assert.NotEqual("home.waiting", persian);
        Assert.Equal(persian, cut.Find("[data-duel-waiting] p").TextContent.Trim());

        var idle = RenderScreen(View("idle"));
        Assert.Equal(Persian("lobby.start"), idle.Find("[data-duel-start]").TextContent.Trim());
    }

    private IRenderedComponent<AsyncDuelScreen> RenderScreen(SessionView view) =>
        Render<AsyncDuelScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(view)));

    private static SessionView View(string phase, DateTimeOffset? deadline = null, JsonObject? body = null, long version = 1)
    {
        var view = new JsonObject
        {
            ["id"] = LobbyViews.SessionId, ["code"] = "ABCD23", ["format"] = "async_duel", ["status"] = "running",
            ["phase"] = phase, ["slot"] = 0, ["step"] = 0, ["version"] = version, ["configVersion"] = 1,
            ["now"] = Now.ToString("O"), ["deadline"] = (deadline ?? Now.AddSeconds(20)).ToString("O"),
            ["me"] = new JsonObject
            {
                ["id"] = "amir", ["seat"] = 0, ["role"] = "player", ["status"] = "active", ["score"] = 0, ["place"] = null, ["outcome"] = null,
            },
            ["roster"] = new JsonObject
            {
                ["total"] = 2,
                ["seats"] = new JsonArray(
                    new JsonObject { ["id"] = "amir", ["name"] = "AMIR", ["seat"] = 0, ["role"] = "player", ["status"] = "active", ["score"] = 0, ["place"] = null, ["outcome"] = null, ["online"] = true },
                    new JsonObject { ["id"] = "sara", ["name"] = "SARA", ["seat"] = 1, ["role"] = "player", ["status"] = "active", ["score"] = 0, ["place"] = null, ["outcome"] = null, ["online"] = true }),
            },
            ["content"] = new JsonObject { ["count"] = 3 },
            ["rules"] = new JsonObject(),
            ["body"] = body?.DeepClone() ?? new JsonObject(),
        };
        return PlatformState.ParseView(view.ToJsonString())!;
    }

    private static JsonObject Body(JsonArray? answers = null, bool reveal = false) => new()
    {
        ["You"] = new JsonObject
        {
            ["Player"] = "amir",
            ["Kind"] = "choice",
            ["KindVersion"] = 1,
            ["Total"] = 3,
            ["Card"] = new JsonObject { ["options"] = new JsonArray("Tehran", "Paris") },
            ["Reveal"] = reveal ? new JsonObject { ["correctIndex"] = 0, ["correctOption"] = "Tehran" } : null,
            ["Answers"] = answers ?? new JsonArray(),
        },
        ["Others"] = new JsonArray(),
    };

    private static JsonObject Answer(double credit, int points, string? stored = null, bool miss = false) => new()
    {
        ["Stored"] = stored is null ? null : JsonNode.Parse(stored), ["IsGraded"] = true, ["Credit"] = credit, ["Points"] = points, ["Miss"] = miss,
    };

    private static JsonObject Body(JsonObject[] answers, bool reveal) => Body(new JsonArray(answers.Select(a => (JsonNode)a).ToArray()), reveal);

    private static string Persian(string key) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", "fa.json")))![key];

    private sealed class FakeCommands : IFormatCommands
    {
        public List<(string Type, object? Payload)> Sent { get; } = [];

        public Task<bool> SendAsync(string type, object? payload = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload));
            return Task.FromResult(true);
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
