using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Features.Reports;
using Quesshi.Web.Plugins.Formats.AsyncDuel;
using Quesshi.Web.Plugins.Formats.Live;
using Quesshi.Web.Plugins.Formats.Poll;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class ReportQuestionTests : BunitContext
{
    private static readonly Guid FirstQuestion = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid SecondQuestion = Guid.Parse("11111111-0000-4000-8000-000000000002");
    private const string SessionId = "7f3c1d52-0000-4000-8000-000000000001";

    private readonly Server server = new();
    private readonly Viewer viewer = new();

    public ReportQuestionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        viewer.Translator = translator;
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
        Services.AddSingleton(http);
        Services.AddSingleton(new ReportsApi(http));
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IClientViewer>(viewer);
        Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
    }

    /// <summary>AC-1.</summary>
    [Fact]
    public void An_async_duel_result_shows_a_report_control_beside_each_reportable_question()
    {
        var cut = Render<AsyncDuelResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("async_duel"))));

        cut.WaitForAssertion(() => Assert.Equal(
            [FirstQuestion.ToString(), SecondQuestion.ToString()],
            cut.FindAll("[data-duel-question-result] [data-report-question]").Select(c => c.GetAttribute("data-report-question")!)));
        Assert.Single(cut.FindAll("[data-duel-question-result='0'] [data-report-open]"));
        Assert.Empty(cut.FindAll("[data-duel-question-result='2'] [data-report-question]"));
    }

    /// <summary>AC-2.</summary>
    [Fact]
    public void A_live_result_shows_a_report_control_beside_each_reportable_question()
    {
        var cut = Render<LiveResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("live"))));

        cut.WaitForAssertion(() => Assert.Equal(
            [FirstQuestion.ToString(), SecondQuestion.ToString()],
            cut.FindAll("[data-reportable-question] [data-report-question]").Select(c => c.GetAttribute("data-report-question")!)));
        Assert.Equal(["Question 0", "Question 1"], cut.FindAll("[data-reportable-question] [data-field='prompt']").Select(p => p.TextContent));
    }

    /// <summary>AC-3.</summary>
    [Fact]
    public void A_poll_result_shows_a_report_control_beside_each_reportable_question()
    {
        var cut = Render<PollResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("poll"))));

        cut.WaitForAssertion(() => Assert.Equal(
            [FirstQuestion.ToString(), SecondQuestion.ToString()],
            cut.FindAll("[data-reportable-question] [data-report-question]").Select(c => c.GetAttribute("data-report-question")!)));
    }

    /// <summary>AC-4.</summary>
    [Fact]
    public void The_history_view_shows_a_report_control_for_a_reportable_question()
    {
        var cut = Render<SessionHistory>(p => p.Add(page => page.Id, Guid.Parse(SessionId)));

        cut.WaitForAssertion(() => Assert.Equal(
            [FirstQuestion.ToString(), SecondQuestion.ToString()],
            cut.FindAll("[data-slot] [data-report-question]").Select(c => c.GetAttribute("data-report-question")!)));
        Assert.Empty(cut.FindAll("[data-slot='2'] [data-report-question]"));
    }

    /// <summary>AC-5.</summary>
    [Fact]
    public void Opening_the_control_offers_the_five_report_reasons()
    {
        var cut = Render<ReportQuestion>(p => p.Add(c => c.QuestionId, FirstQuestion));
        Open(cut);

        Assert.Equal(["wronganswer", "unclear", "duplicate", "offensive", "other"],
            cut.FindAll("[data-report-reason]").Select(b => b.GetAttribute("data-report-reason")!));
        Assert.Equal(["The answer is wrong", "Unclear, or more than one answer works", "It's a duplicate", "It's offensive", "Something else"],
            cut.FindAll("[data-report-reason]").Select(b => b.TextContent));
    }

    /// <summary>AC-6.</summary>
    [Fact]
    public void Choosing_a_reason_posts_it_for_the_question_and_shows_the_thank_you()
    {
        var cut = Render<ReportQuestion>(p => p.Add(c => c.QuestionId, SecondQuestion));
        Open(cut);

        cut.Find("[data-report-reason='duplicate']").Click();

        cut.WaitForAssertion(() => Assert.Equal("Thanks — we'll look at it.", cut.Find("[data-report-thanks]").TextContent));
        var post = Assert.Single(server.Posts);
        Assert.Equal("/api/x/reports", post.Path);
        Assert.Equal("csrf-token", post.Csrf);
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal(SecondQuestion.ToString(), body.RootElement.GetProperty("questionId").GetString());
        Assert.Equal("duplicate", body.RootElement.GetProperty("reason").GetString());
        Assert.Empty(cut.FindAll("[data-report-reason]"));
    }

    [Fact]
    public void A_refused_report_keeps_the_reasons_open_and_shows_an_error()
    {
        server.ReportStatus = HttpStatusCode.BadRequest;
        var cut = Render<ReportQuestion>(p => p.Add(c => c.QuestionId, FirstQuestion));
        Open(cut);

        cut.Find("[data-report-reason='other']").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-report-error]")));
        Assert.Empty(cut.FindAll("[data-report-thanks]"));
        Assert.Equal(5, cut.FindAll("[data-report-reason]").Count);
    }

    /// <summary>AC-7.</summary>
    [Fact]
    public void A_guest_sees_no_report_control_on_any_reportable_question_view()
    {
        viewer.IsGuest = true;

        var duel = Render<AsyncDuelResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("async_duel"))));
        var live = Render<LiveResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("live"))));
        var history = Render<SessionHistory>(p => p.Add(page => page.Id, Guid.Parse(SessionId)));

        duel.WaitForAssertion(() => Assert.Equal(3, duel.FindAll("[data-duel-question-result]").Count));
        history.WaitForAssertion(() => Assert.Equal(3, history.FindAll("[data-slot]").Count));
        Assert.Empty(duel.FindAll("[data-report-question]"));
        Assert.Empty(history.FindAll("[data-report-question]"));
        Assert.Empty(live.FindAll("[data-report-question]"));
        Assert.DoesNotContain(server.Requests, path => path.Contains("/history", StringComparison.Ordinal) && path.Contains("limit=50", StringComparison.Ordinal));
    }

    /// <summary>AC-8.</summary>
    [Fact]
    public void A_signed_in_player_sees_no_report_control_while_the_reports_capability_is_off()
    {
        server.ReportsOn = false;

        var duel = Render<AsyncDuelResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("async_duel"))));
        var live = Render<LiveResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View("live"))));
        var history = Render<SessionHistory>(p => p.Add(page => page.Id, Guid.Parse(SessionId)));

        duel.WaitForAssertion(() => Assert.Equal(3, duel.FindAll("[data-duel-question-result]").Count));
        history.WaitForAssertion(() => Assert.Equal(3, history.FindAll("[data-slot]").Count));
        Assert.Empty(duel.FindAll("[data-report-question]"));
        Assert.Empty(history.FindAll("[data-report-question]"));
        Assert.Empty(live.FindAll("[data-report-question]"));
        Assert.Empty(server.Posts);
    }

    private static void Open(IRenderedComponent<ReportQuestion> cut)
    {
        cut.WaitForElement("[data-report-open]").Click();
    }

    private static SessionView View(string format) => PlatformState.ParseView($$$"""
        {"id":"{{{SessionId}}}","code":"ABCD23","format":"{{{format}}}","status":"finished","phase":"done","slot":-1,"step":0,"version":3,"configVersion":1,
         "now":"2026-01-01T00:00:00Z","deadline":null,
         "me":{"id":"amir","seat":0,"role":"player","status":"done","score":1,"place":1,"outcome":"win"},
         "roster":{"total":1,"seats":[{"id":"amir","name":"amir","seat":0,"role":"player","status":"done","score":1,"place":1,"outcome":"win","online":false}]},
         "content":{},"rules":{},"body":{}}
        """)!;

    private sealed class Viewer : IClientViewer
    {
        public Translator Translator { get; set; } = null!;
        public bool IsGuest { get; set; }
        public string LanguageCode => "en";
        public string Direction => "ltr";
        public string Num(object? value) => Translator.Num(value);
    }

    private sealed record Post(string Path, string Body, string? Csrf);

    /// <summary>Serves translations, the tenant capabilities, a three-question history (the last without a bank question) and the reports route.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public bool ReportsOn { get; set; } = true;
        public HttpStatusCode ReportStatus { get; set; } = HttpStatusCode.OK;
        public List<string> Requests { get; } = [];
        public List<Post> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/i18n/", StringComparison.Ordinal))
            {
                var file = Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(path));
                return File.Exists(file) ? Json(File.ReadAllText(file)) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Requests.Add(request.RequestUri.PathAndQuery);
            if (path == "/api/config") return Json(ReportsOn ? """{"capabilities":{"feature":["reports"]}}""" : """{"capabilities":{"feature":[]}}""");
            if (path == "/api/antiforgery") return Json("""{"token":"csrf-token"}""");
            if (path == "/api/x/reports" && request.Method == HttpMethod.Post)
            {
                Posts.Add(new Post(path, await request.Content!.ReadAsStringAsync(cancellationToken),
                    request.Headers.TryGetValues("X-CSRF", out var values) ? values.Single() : null));
                return Json("""{"reported":true,"alreadyReported":false}""", ReportStatus);
            }

            if (path == $"/api/sessions/{SessionId}/history") return Json(History);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string History => $$"""
            {"items":[
              {{Item(0, FirstQuestion)}},{{Item(1, SecondQuestion)}},{{Item(2, null)}}],"next":null}
            """;

        private static string Item(int slot, Guid? question) => $$$"""
            {"slot":{{{slot}}},"kind":"choice","kindVersion":1,"prompt":"Question {{{slot}}}","card":{"options":["Tehran","Paris"]},
             "reveal":{"correctIndex":0,"correctOption":"Tehran"},"answers":[{"index":0}]{{{(question is { } id ? $",\"questionId\":\"{id}\"" : "")}}}}
            """;

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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
