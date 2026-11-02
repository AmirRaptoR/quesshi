using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Poll;
using Quesshi.Web.Services;
using static Quesshi.Web.Tests.LiveScreenViews;

namespace Quesshi.Web.Tests;

public sealed class PollScreenTests : BunitContext
{
    private static readonly Player Amir = new("amir");
    private static readonly Player Sara = new("sara");
    private static readonly Player Omid = new("omid");
    private static readonly Player[] Three = [Amir, Sara, Omid];

    private readonly FakeCommands commands = new();

    public PollScreenTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(new Tables()) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IFormatCommands>(commands);
    }

    [Fact]
    public void A_poll_choice_slot_renders_its_card_through_KindPlay()
    {
        var cut = RenderScreen(View(ChoiceBody()));

        cut.WaitForAssertion(() => Assert.Equal(["a", "b"], cut.FindAll("[data-kind='poll_choice'] [data-option]").Select(button => button.GetAttribute("data-option"))));
        Assert.Equal(["Red", "Blue"], cut.FindAll("[data-kind='poll_choice'] [data-option]").Select(button => button.TextContent));
    }

    [Fact]
    public void A_poll_roster_slot_renders_its_card_through_KindPlay()
    {
        var cut = RenderScreen(View(RosterBody()));

        cut.WaitForAssertion(() => Assert.Equal(["0", "1"], cut.FindAll("[data-kind='poll_roster'] [data-seat]").Select(button => button.GetAttribute("data-seat"))));
        Assert.Single(cut.FindAll("[data-kind='poll_roster'] [data-none]"));
    }

    [Fact]
    public async Task The_not_applicable_choice_is_sent_as_the_viewers_answer()
    {
        var cut = RenderScreen(View(ChoiceBody()));
        cut.WaitForElement("[data-kind='poll_choice'] [data-not-applicable]");

        await cut.InvokeAsync(() => cut.Find("[data-kind='poll_choice'] [data-not-applicable]").ClickAsync());

        var sent = Assert.Single(commands.Sent);
        Assert.Equal("answer", sent.Type);
        Assert.Equal("""{"notApplicable":true}""", ((JsonElement)sent.Payload!).GetRawText());
    }

    [Fact]
    public void The_screen_shows_the_viewers_stored_choice_and_which_active_players_have_answered()
    {
        var cut = RenderScreen(View(ChoiceBody(answer: """{"option":"b"}""", revision: 1, answered: ["amir", "sara"]), players: Three));

        Assert.Equal("Blue", cut.Find("[data-poll-stored] strong").TextContent);
        Assert.Equal("true", cut.Find("[data-seat='amir']").GetAttribute("data-answered"));
        Assert.Equal("true", cut.Find("[data-seat='sara']").GetAttribute("data-answered"));
        Assert.Equal("false", cut.Find("[data-seat='omid']").GetAttribute("data-answered"));
    }

    [Fact]
    public void A_player_who_left_is_not_listed_among_the_active_players()
    {
        var cut = RenderScreen(View(ChoiceBody(answered: ["amir"]), players: [Amir, Sara, Omid with { Status = "left" }]));

        Assert.Equal(["amir", "sara"], cut.FindAll("[data-poll-answered-list] [data-seat]").Select(seat => seat.GetAttribute("data-seat")));
    }

    [Fact]
    public void After_answering_the_screen_waits_with_no_scores_and_no_clock()
    {
        var cut = RenderScreen(View(ChoiceBody(answer: """{"option":"a"}""", revision: 1, answered: ["amir"]), players: Three));

        Assert.Single(cut.FindAll("[data-poll-waiting]"));
        Assert.Contains("1 of 3", cut.Find("[data-poll-waiting]").TextContent);
        Assert.Empty(cut.FindAll("[data-score-gain]"));
        Assert.Empty(cut.FindAll("svg.ring"));
        Assert.Empty(cut.FindAll("[data-live-ring]"));
    }

    [Fact]
    public void Before_answering_the_screen_does_not_show_the_waiting_state()
    {
        var cut = RenderScreen(View(ChoiceBody(), players: Three));

        Assert.Empty(cut.FindAll("[data-poll-waiting]"));
        Assert.Empty(cut.FindAll("[data-poll-stored]"));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 5)]
    public async Task A_choice_is_sent_with_a_revision_one_higher_than_the_stored_one(int? stored, int expected)
    {
        var body = stored is { } revision ? ChoiceBody(answer: """{"option":"a"}""", revision: revision) : ChoiceBody();
        var cut = RenderScreen(View(body));
        cut.WaitForElement("[data-option='b']");

        await cut.InvokeAsync(() => cut.Find("[data-option='b']").ClickAsync());

        var sent = Assert.Single(commands.Sent);
        Assert.Equal(expected, sent.Revision);
        Assert.Equal("""{"option":"b"}""", ((JsonElement)sent.Payload!).GetRawText());
    }

    [Fact]
    public async Task A_player_can_change_a_choice_after_answering_while_the_slot_is_open()
    {
        var cut = RenderScreen(View(ChoiceBody(answer: """{"option":"a"}""", revision: 1, answered: ["amir"]), players: Three));
        cut.WaitForElement("[data-option='b']");

        Assert.Null(cut.Find("[data-option='b']").GetAttribute("disabled"));
        await cut.InvokeAsync(() => cut.Find("[data-option='b']").ClickAsync());

        Assert.Equal(2, Assert.Single(commands.Sent).Revision);
    }

    [Fact]
    public async Task A_retried_send_keeps_the_same_choice_and_revision()
    {
        var cut = RenderScreen(View(ChoiceBody(answer: """{"option":"a"}""", revision: 2)));
        cut.WaitForElement("[data-option='b']");
        commands.Accept = false;
        await cut.InvokeAsync(() => cut.Find("[data-option='b']").ClickAsync());
        Assert.Single(cut.FindAll("[data-poll-send-failed]"));

        commands.Accept = true;
        await cut.InvokeAsync(() => cut.Find("[data-poll-retry]").ClickAsync());

        Assert.Equal(2, commands.Sent.Count);
        Assert.Equal(commands.Sent[0].Revision, commands.Sent[1].Revision);
        Assert.Equal(3, commands.Sent[1].Revision);
        Assert.Equal(((JsonElement)commands.Sent[0].Payload!).GetRawText(), ((JsonElement)commands.Sent[1].Payload!).GetRawText());
        Assert.Empty(cut.FindAll("[data-poll-send-failed]"));
    }

    [Fact]
    public void Reopening_the_session_in_a_second_tab_starts_from_the_stored_answer_and_revision()
    {
        var view = View(ChoiceBody(answer: """{"option":"b"}""", revision: 3));

        var cut = RenderScreen(view);

        Assert.Equal("Blue", cut.Find("[data-poll-stored] strong").TextContent);
        Assert.Single(cut.FindAll("[data-poll-waiting]"));
    }

    [Fact]
    public async Task A_second_tabs_change_is_sent_above_the_revision_the_first_tab_stored()
    {
        var secondTab = RenderScreen(View(ChoiceBody(answer: """{"option":"b"}""", revision: 3)));
        secondTab.WaitForElement("[data-option='a']");

        await secondTab.InvokeAsync(() => secondTab.Find("[data-option='a']").ClickAsync());

        Assert.Equal(4, Assert.Single(commands.Sent).Revision);
    }

    [Fact]
    public async Task A_revision_conflict_shows_the_authoritative_stored_choice_and_not_the_stale_one()
    {
        var cut = RenderScreen(View(ChoiceBody(answer: """{"option":"a"}""", revision: 1)));
        cut.WaitForElement("[data-option='b']");
        await cut.InvokeAsync(() => cut.Find("[data-option='b']").ClickAsync());

        // The server answered with its current view: another tab had already stored "b"... and then "a" at revision 5.
        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(ChoiceBody(answer: """{"option":"a"}""", revision: 5), version: 2))));

        Assert.Equal("Red", cut.Find("[data-poll-stored] strong").TextContent);
        Assert.Empty(cut.FindAll("[data-poll-send-failed]"));
    }

    [Fact]
    public async Task A_new_slot_clears_a_pending_failed_send()
    {
        var cut = RenderScreen(View(ChoiceBody()));
        cut.WaitForElement("[data-option='b']");
        commands.Accept = false;
        await cut.InvokeAsync(() => cut.Find("[data-option='b']").ClickAsync());

        cut.Render(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(ChoiceBody(), slot: 1, version: 2))));

        Assert.Empty(cut.FindAll("[data-poll-send-failed]"));
    }

    [Fact]
    public void Another_players_choice_never_appears_on_the_screen()
    {
        var cut = RenderScreen(View(ChoiceBody(answer: """{"option":"a"}""", revision: 1, answered: ["amir", "sara"]), players: Three));

        Assert.DoesNotContain("Blue", cut.Find("[data-seat='sara']").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-poll-stored]"));
        Assert.Equal("Red", cut.Find("[data-poll-stored] strong").TextContent);
    }

    private IRenderedComponent<PollScreen> RenderScreen(SessionView view) =>
        Render<PollScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(view)));

    private static SessionView View(JsonObject body, Player[]? players = null, int slot = 0, long version = 1) =>
        Parse("amir", players ?? [Amir, Sara], 0, slot: slot, body: body, total: 3, version: version);

    private static JsonObject ChoiceBody(string? answer = null, int? revision = null, string[]? answered = null) => new()
    {
        ["Card"] = JsonNode.Parse("""{"options":[{"id":"a","text":"Red"},{"id":"b","text":"Blue"}]}"""),
        ["Answer"] = answer is null ? null : JsonNode.Parse(answer),
        ["Revision"] = revision,
        ["AnsweredSeats"] = new JsonArray((answered ?? []).Select(id => (JsonNode)id).ToArray()),
        ["Kind"] = "poll_choice",
        ["KindVersion"] = 1,
        ["Slot"] = 0,
        ["TotalSlots"] = 3,
    };

    private static JsonObject RosterBody() => new()
    {
        ["Card"] = JsonNode.Parse("""{"options":[{"seat":0,"name":"AMIR"},{"seat":1,"name":"SARA"}]}"""),
        ["AnsweredSeats"] = new JsonArray(),
        ["Kind"] = "poll_roster",
        ["KindVersion"] = 1,
        ["Slot"] = 0,
        ["TotalSlots"] = 3,
    };

    private sealed class FakeCommands : IFormatCommands
    {
        public List<(string Type, object? Payload, int? Revision)> Sent { get; } = [];
        public bool Accept { get; set; } = true;

        public Task<bool> SendAsync(string type, object? payload = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload, null));
            return Task.FromResult(Accept);
        }

        public Task<bool> SendAsync(string type, object? payload, int revision, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload, revision));
            return Task.FromResult(Accept);
        }
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
