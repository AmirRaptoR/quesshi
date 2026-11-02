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

public sealed class AsyncDuelResultTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly History history = new();
    private readonly Translator translator;

    public AsyncDuelResultTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(history) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        translator = new Translator(http);
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
        Services.AddSingleton(http);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IClientViewer>(new TranslatorViewer(translator));
    }

    [Fact]
    public void The_result_lists_the_standings_of_the_finished_duel()
    {
        var cut = RenderResult(View(total: 3));

        var rows = cut.FindAll("[data-standing]");
        Assert.Equal(["sara", "amir"], rows.Select(row => row.GetAttribute("data-standing")));
        Assert.Equal("1", cut.Find("[data-standing='sara'] [data-standing-place]").TextContent.Trim().Trim('s', 't', 'n', 'd', 'r', 'h'));
        Assert.Equal(2, cut.FindAll(".duelbar__score").Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void A_duel_of_twenty_questions_or_fewer_renders_one_ring_of_rays(int total)
    {
        var cut = RenderResult(View(total));

        Assert.Equal("rays", cut.Find("[data-duel-shamseh]").GetAttribute("data-duel-shamseh"));
        Assert.Equal(total * 2, cut.FindAll("svg.shamseh .shamseh__ray").Count);
        Assert.Empty(cut.FindAll("svg.shamseh .shamseh__rings"));
    }

    [Fact]
    public void A_duel_of_more_than_twenty_questions_renders_two_rings()
    {
        var cut = RenderResult(View(21));

        Assert.Equal("rings", cut.Find("[data-duel-shamseh]").GetAttribute("data-duel-shamseh"));
        Assert.Single(cut.FindAll("svg.shamseh .shamseh__rings"));
        Assert.Equal(2, cut.FindAll("svg.shamseh .shamseh__track").Count);
        Assert.Empty(cut.FindAll("svg.shamseh .shamseh__ray"));
    }

    [Fact]
    public void Each_question_shows_both_players_answers_through_KindReveal()
    {
        history.Questions = 2;

        var cut = RenderResult(View(2));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-duel-question-result]").Count));
        var first = cut.Find("[data-duel-question-result='0']");
        cut.WaitForAssertion(() => Assert.Equal(2, first.QuerySelectorAll("[data-kind='choice']").Length));
        Assert.Equal("0", first.QuerySelector("[data-answers-of='amir'] [data-answer-index]")!.GetAttribute("data-answer-index"));
        Assert.Equal("1", first.QuerySelector("[data-answers-of='sara'] [data-answer-index]")!.GetAttribute("data-answer-index"));
    }

    [Fact]
    public void A_hundred_question_duel_requests_one_page_of_history_at_a_time()
    {
        history.Questions = 100;

        var cut = RenderResult(View(100));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-duel-question-result]")));
        var request = Assert.Single(history.Requests);
        Assert.Contains("limit=10", request);
        Assert.DoesNotContain("after=", request);
        Assert.Equal(10, cut.FindAll("[data-duel-question-result]").Count);
    }

    [Fact]
    public void Requesting_a_further_page_appends_the_next_questions_to_those_already_shown()
    {
        history.Questions = 25;
        var cut = RenderResult(View(25));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-duel-more]").HasAttribute("disabled")));
        Assert.Equal(10, cut.FindAll("[data-duel-question-result]").Count);

        cut.Find("[data-duel-more]").Click();

        cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll("[data-duel-question-result]").Count));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()),
            cut.FindAll("[data-duel-question-result]").Select(item => item.GetAttribute("data-duel-question-result")));
        Assert.EndsWith("after=9", history.Requests[1]);
    }

    [Fact]
    public async Task The_result_renders_right_to_left_with_translated_texts_in_an_rtl_tenant_language()
    {
        await translator.UseAsync("fa");
        translator.Direction = "rtl";

        var cut = RenderResult(View(3));

        Assert.Equal("rtl", cut.Find("[data-duel-result]").GetAttribute("dir"));
        var persian = Persian("result.standings");
        Assert.NotEqual("result.standings", persian);
        Assert.Equal(persian, cut.Find("[data-duel-result] h2").TextContent.Trim());
    }

    private IRenderedComponent<AsyncDuelResult> RenderResult(SessionView view) =>
        Render<AsyncDuelResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(view)));

    private static SessionView View(int total)
    {
        JsonObject Run(string player, int index) => new()
        {
            ["Player"] = player,
            ["Total"] = total,
            ["Answers"] = new JsonArray(Enumerable.Range(0, total).Select(slot => (JsonNode)new JsonObject
            {
                ["Stored"] = new JsonObject { ["index"] = index },
                ["IsGraded"] = true, ["Credit"] = index == 0 ? 1 : 0, ["Points"] = index == 0 ? 80 : 0, ["Miss"] = false,
            }).ToArray()),
        };
        var view = new JsonObject
        {
            ["id"] = LobbyViews.SessionId, ["code"] = "ABCD23", ["format"] = "async_duel", ["status"] = "finished",
            ["phase"] = "2", ["slot"] = total - 1, ["step"] = 0, ["version"] = 1, ["configVersion"] = 1,
            ["now"] = Now.ToString("O"), ["deadline"] = null,
            ["me"] = new JsonObject { ["id"] = "amir", ["seat"] = 0, ["role"] = "player", ["status"] = "done", ["score"] = 5, ["place"] = 2, ["outcome"] = "loss" },
            ["roster"] = new JsonObject
            {
                ["total"] = 2,
                ["seats"] = new JsonArray(
                    new JsonObject { ["id"] = "amir", ["name"] = "AMIR", ["seat"] = 0, ["role"] = "player", ["status"] = "done", ["score"] = 5, ["place"] = 2, ["outcome"] = "loss", ["online"] = true },
                    new JsonObject { ["id"] = "sara", ["name"] = "SARA", ["seat"] = 1, ["role"] = "player", ["status"] = "done", ["score"] = 9, ["place"] = 1, ["outcome"] = "win", ["online"] = true }),
            },
            ["content"] = new JsonObject { ["count"] = total },
            ["rules"] = new JsonObject(),
            ["body"] = new JsonObject { ["You"] = Run("amir", 0), ["Others"] = new JsonArray(Run("sara", 1)) },
        };
        return PlatformState.ParseView(view.ToJsonString())!;
    }

    private static string Persian(string key) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "i18n", "fa.json")))![key];

    /// <summary>Serves translation tables and a paged history with the real endpoint's <c>after</c>/<c>limit</c> contract.</summary>
    private sealed class History : HttpMessageHandler
    {
        public int Questions { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/i18n/", StringComparison.Ordinal))
            {
                var file = Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(path));
                return Task.FromResult(File.Exists(file)
                    ? Json(File.ReadAllText(file))
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Requests.Add(request.RequestUri.PathAndQuery);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var after = int.TryParse(query["after"], out var cursor) ? cursor : -1;
            var limit = int.Parse(query["limit"] ?? "20");
            var slots = Enumerable.Range(after + 1, Math.Max(0, Questions - after - 1)).Take(limit).ToArray();
            var items = new JsonArray(slots.Select(slot => (JsonNode)new JsonObject
            {
                ["slot"] = slot, ["kind"] = "choice", ["kindVersion"] = 1, ["prompt"] = $"Question {slot}",
                ["card"] = new JsonObject { ["options"] = new JsonArray("Tehran", "Paris") },
                ["reveal"] = new JsonObject { ["correctIndex"] = 0, ["correctOption"] = "Tehran" },
                ["answers"] = new JsonArray(new JsonObject { ["index"] = 0 }),
            }).ToArray());
            var hasMore = slots.Length > 0 && slots[^1] < Questions - 1;
            return Task.FromResult(Json(new JsonObject { ["items"] = items, ["next"] = hasMore ? slots[^1] : null }.ToJsonString()));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class TranslatorViewer(Translator translator) : IClientViewer
    {
        public string LanguageCode => translator.Lang;
        public string Direction => translator.Direction;
        public bool IsGuest => false;
        public string Num(object? value) => translator.Num(value);
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
