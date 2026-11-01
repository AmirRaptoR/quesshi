using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class ReportedPageTests : BunitContext
{
    private const string First = "11111111-1111-1111-1111-111111111111";
    private const string Second = "22222222-2222-2222-2222-222222222222";
    private readonly AdminClientRegistry registry = new(new EmptyLoader(), new EmptyTranslationRegistry());

    public ReportedPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
        Services.AddSingleton<IAdminClientRegistry>(registry);
        new ReportsAdminClientEntry().Register(registry);
    }

    [Fact]
    public async Task Moderator_sees_Reported_navigation_when_reports_are_enabled()
    {
        var state = await StateAsync(new Handler("""{"capabilities":{"feature":["reports"]}}"""));
        var cut = Render<AdminNav>(p => p.AddCascadingValue(state));

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/reports" && a.TextContent == "admin.reported.nav"));
    }

    [Fact]
    public async Task Moderator_does_not_see_Reported_navigation_when_reports_are_disabled()
    {
        var state = await StateAsync(new Handler("""{"capabilities":{"feature":["stats"]}}"""));
        var cut = Render<AdminNav>(p => p.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("a").Count > 0);
        Assert.DoesNotContain(cut.FindAll("a"), a => a.GetAttribute("href") == "/admin/feature/reports");
    }

    [Fact]
    public async Task Reported_page_shows_report_count_for_each_question()
    {
        var cut = await RenderPageAsync();

        cut.WaitForAssertion(() => Assert.Equal(["3", "1"], [cut.Find($"[data-testid=reported-count-{First}]").TextContent, cut.Find($"[data-testid=reported-count-{Second}]").TextContent]));
    }

    [Fact]
    public async Task Reported_page_shows_recorded_reasons_for_each_question()
    {
        var cut = await RenderPageAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["WrongAnswer", "Unclear"], cut.FindAll($"[data-testid=reported-reasons-{First}] .chip").Select(chip => chip.TextContent));
            Assert.Equal(["Offensive"], cut.FindAll($"[data-testid=reported-reasons-{Second}] .chip").Select(chip => chip.TextContent));
        });
    }

    [Fact]
    public async Task Reported_page_keeps_the_api_most_reported_first_order()
    {
        var cut = await RenderPageAsync();

        cut.WaitForAssertion(() => Assert.Equal(
            [$"reported-{First}", $"reported-{Second}"],
            cut.FindAll("article").Select(article => article.GetAttribute("data-testid"))));
    }

    [Fact]
    public async Task Dismissing_a_question_removes_it_from_the_refreshed_list()
    {
        var handler = new Handler("{}");
        var cut = await RenderPageAsync(handler);
        cut.WaitForState(() => cut.FindAll($"[data-testid=dismiss-reported-{First}]").Count == 1);

        cut.Find($"[data-testid=dismiss-reported-{First}]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"[data-testid=reported-{First}]")));
        Assert.NotEmpty(cut.FindAll($"[data-testid=reported-{Second}]"));
        Assert.Contains((HttpMethod.Post, $"/api/admin/x/reports/{First}/dismiss"), handler.Requests);
    }

    private async Task<IRenderedComponent<Reported>> RenderPageAsync(Handler? handler = null)
    {
        var state = await StateAsync(handler ?? new Handler("{}"));
        return Render<Reported>(p => p.AddCascadingValue(state));
    }

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private sealed class Handler(string config) : HttpMessageHandler
    {
        private bool dismissed;

        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path));
            if (request.Method == HttpMethod.Post && path.EndsWith("/dismiss", StringComparison.Ordinal)) dismissed = true;
            var json = path switch
            {
                "/api/config" => config,
                "/api/admin/antiforgery" => """{"token":"csrf"}""",
                "/api/admin/x/reports" when request.Method == HttpMethod.Get => dismissed
                    ? $$"""[{"questionId":"{{Second}}","count":1,"reasons":["Offensive"]}]"""
                    : $$"""[{"questionId":"{{First}}","count":3,"reasons":["WrongAnswer","Unclear"]},{"questionId":"{{Second}}","count":1,"reasons":["Offensive"]}]""",
                _ when path.StartsWith("/api/admin/questions/", StringComparison.Ordinal) => """{"id":"11111111-1111-1111-1111-111111111111","kind":"choice","kindVersion":1,"family":"trivia","language":"en","categoryId":null,"level":1,"prompt":"A prompt","payload":{},"explanation":null,"topic":null,"status":"approved","source":"admin","ownerId":null,"mediaId":null}""",
                _ when request.Method == HttpMethod.Post => "{}",
                _ => """{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"Moderator","isPlatform":false,"mustChangePassword":false}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class EmptyLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }

    private sealed class EmptyTranslationRegistry : IClientTranslationRegistry
    {
        public void Register(ClientTranslation translation) { }
    }

    private sealed class Translator : IClientTranslator
    {
        public string this[string key] => key;
    }
}
