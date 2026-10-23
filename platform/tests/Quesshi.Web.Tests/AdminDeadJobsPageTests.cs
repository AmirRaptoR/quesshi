using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AdminDeadJobsPageTests : BunitContext
{
    public AdminDeadJobsPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
    }

    [Fact]
    public async Task Owner_dead_jobs_screen_renders_and_refreshes_the_rerun_job_status()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson()),
            Response(HttpStatusCode.OK, """[{"id":42,"kind":"guest-purge","attempts":3,"lastError":"seeded error","occurredAt":"2026-10-22T12:00:00+00:00","status":"dead"}]"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, """{"id":42,"kind":"guest-purge","attempts":0,"lastError":null,"occurredAt":null,"status":"ready"}"""),
            Response(HttpStatusCode.OK, """{"id":42,"kind":"guest-purge","attempts":1,"lastError":null,"occurredAt":"2026-10-22T12:01:00+00:00","status":"done"}"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminDeadJobs>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("[data-testid=rerun-dead-job-42]").Count == 1);
        cut.Find("[data-testid=rerun-dead-job-42]").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid=refresh-dead-job-42]").Count == 1);
        cut.Find("[data-testid=refresh-dead-job-42]").Click();
        cut.WaitForAssertion(() => Assert.Contains("done", cut.Find("[data-testid=dead-job-status-42]").TextContent, StringComparison.Ordinal));

        Assert.Contains((HttpMethod.Post, "/api/admin/dead-jobs/42/rerun"), handler.Requests);
    }

    [Fact]
    public async Task Moderator_cannot_open_dead_job_recovery_and_navigation_excludes_the_role()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Moderator")));
        var state = await StateAsync(handler);
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new AdminDeadJobsClientEntry().Register(registry);

        var cut = Render<AdminDeadJobs>(parameters => parameters.AddCascadingValue(state));

        Assert.Equal((0, false), (cut.FindAll("button").Count, registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false))));
    }

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private static string SessionJson(string role = "Owner") => $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";
    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status) { Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            return Task.FromResult(responses.Dequeue());
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
