using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class SessionHistoryPageTests : BunitContext
{
    private const string SessionId = "7f3c1d52-0000-4000-8000-000000000001";

    /// <summary>AC-1.</summary>
    [Fact]
    public void The_sessions_page_lists_the_players_sessions_in_the_order_the_history_endpoint_returns_them()
    {
        var handler = new History(
            ("/api/me/sessions", """
                {"sessions":[
                  {"id":"00000000-0000-4000-8000-00000000000b","format":"poll","status":"finished","joinedAt":"2026-03-02T10:00:00+00:00","score":4,"place":1,"outcome":"win"},
                  {"id":"00000000-0000-4000-8000-00000000000a","format":"live","status":"running","joinedAt":"2026-03-01T10:00:00+00:00","score":0,"place":null,"outcome":null}],
                 "next":"cursor-1"}
                """),
            ("/api/me/sessions?before=cursor-1", """
                {"sessions":[{"id":"00000000-0000-4000-8000-00000000000c","format":"async_duel","status":"finished","joinedAt":"2026-02-01T10:00:00+00:00","score":1,"place":2,"outcome":"loss"}],"next":null}
                """));
        Use(handler);

        var cut = Render<Sessions>();

        cut.WaitForAssertion(() => Assert.Equal(
            ["00000000-0000-4000-8000-00000000000b", "00000000-0000-4000-8000-00000000000a"],
            cut.FindAll("[data-session-id]").Select(row => row.GetAttribute("data-session-id")!)));
        Assert.Equal(["poll", "live"], cut.FindAll("[data-field='format']").Select(format => format.TextContent));
        Assert.Equal("/sessions/00000000-0000-4000-8000-00000000000b", cut.Find("[data-session-id] a").GetAttribute("href"));

        cut.Find("[data-test='sessions-more']").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            ["poll", "live", "async_duel"],
            cut.FindAll("[data-field='format']").Select(format => format.TextContent)));
        Assert.Empty(cut.FindAll("[data-test='sessions-more']"));
    }

    [Fact]
    public void The_sessions_page_tells_a_new_player_they_have_no_sessions()
    {
        Use(new History(("/api/me/sessions", """{"sessions":[],"next":null}""")));

        var cut = Render<Sessions>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='sessions-empty']")));
    }

    /// <summary>AC-4.</summary>
    [Fact]
    public void The_session_page_shows_each_question_in_the_persisted_slot_order_through_its_kind_client()
    {
        Use(new History(
            ($"/api/sessions/{SessionId}/history", """
                {"items":[
                  {"slot":0,"kind":"test","kindVersion":1,"prompt":"First question","card":{"c":"card0"},"reveal":{"r":"reveal0"},"answers":[{"index":1}]},
                  {"slot":1,"kind":"test","kindVersion":1,"prompt":"Second question","card":{"c":"card1"},"reveal":{"r":"reveal1"},"answers":[]}],
                 "next":1}
                """),
            ($"/api/sessions/{SessionId}/history?after=1", """
                {"items":[{"slot":2,"kind":"test","kindVersion":1,"prompt":"Third question","card":{"c":"card2"},"reveal":{"r":"reveal2"},"answers":[]}],"next":null}
                """)));

        var cut = Render<SessionHistory>(parameters => parameters.Add(page => page.Id, Guid.Parse(SessionId)));

        cut.WaitForAssertion(() => Assert.Equal(
            ["First question", "Second question"], cut.FindAll("[data-field='prompt']").Select(prompt => prompt.TextContent)));
        Assert.Equal(["0", "1"], cut.FindAll("[data-slot]").Select(item => item.GetAttribute("data-slot")!));
        var first = cut.Find("[data-slot='0'] [data-kind='test']").TextContent;
        Assert.Contains("card0", first);
        Assert.Contains("reveal0", first);
        Assert.Contains("\"index\":1", first);

        cut.Find("[data-test='questions-more']").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            ["First question", "Second question", "Third question"], cut.FindAll("[data-field='prompt']").Select(prompt => prompt.TextContent)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public void The_session_page_reports_unavailable_history_without_listing_questions(HttpStatusCode status)
    {
        Use(new History($"/api/sessions/{SessionId}/history", "{}", status));

        var cut = Render<SessionHistory>(parameters => parameters.Add(page => page.Id, Guid.Parse(SessionId)));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='history-unavailable']")));
        Assert.Empty(cut.FindAll("[data-slot]"));
    }

    private void Use(History handler)
    {
        var http = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        Services.AddSingleton(http);
        Services.AddSingleton(translator);
        var registry = new ClientRegistry(new NoLoader());
        registry.Register(new TestKindClient());
        Services.AddSingleton<IClientRegistry>(registry);
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class History : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Json, HttpStatusCode Status)> table = [];

        public History(params (string Path, string Json)[] routes)
        {
            foreach (var route in routes) table[route.Path] = (route.Json, HttpStatusCode.OK);
        }

        public History(string path, string json, HttpStatusCode status) => table[path] = (json, status);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            if (key.StartsWith("/i18n/", StringComparison.Ordinal)) return Task.FromResult(Json("{}", HttpStatusCode.OK));
            return Task.FromResult(table.TryGetValue(key, out var found) ? Json(found.Json, found.Status) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
