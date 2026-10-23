using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;
using Xunit;

namespace Quesshi.Web.Tests;

public sealed class AdminAuditPageTests : BunitContext
{
    [Fact]
    public async Task Owner_audit_screen_renders_action_actor_target_and_time_from_the_audit_endpoint()
    {
        var actorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var targetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var at = DateTimeOffset.Parse("2026-10-22T17:20:00Z", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        var handler = new QueueHandler(
        [
            SessionResponse("Owner"),
            JsonResponse($$"""{"items":[{"id":12,"actorType":"account","actorId":"{{actorId}}","action":"admin.category.create","targetType":"category","targetId":"{{targetId}}","at":"{{at:O}}"}],"next":null}"""),
        ]);
        var state = await CreateStateAsync(handler);

        var cut = Render<AdminAudit>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForState(() => cut.FindAll("[data-testid='audit-entry']").Count == 1);

        var row = cut.Find("[data-testid='audit-entry']");
        Assert.Equal(
            ("admin.category.create", $"account · {actorId}", $"category · {targetId}", at.ToString("O")),
            (row.QuerySelector("[data-testid='audit-action']")!.TextContent.Trim(),
             row.QuerySelector("[data-testid='audit-actor']")!.TextContent.Trim(),
             row.QuerySelector("[data-testid='audit-target']")!.TextContent.Trim(),
             row.QuerySelector("time")!.GetAttribute("datetime")));
    }

    [Fact]
    public async Task Owner_audit_screen_appends_older_events_when_requested()
    {
        var handler = new QueueHandler(
        [
            SessionResponse("Owner"),
            JsonResponse("""{"items":[{"id":12,"actorType":"account","action":"newer","at":"2026-10-22T17:20:00.0000000+00:00"}],"next":11}"""),
            JsonResponse("""{"items":[{"id":11,"actorType":"account","action":"older","at":"2026-10-22T17:19:00.0000000+00:00"}],"next":null}"""),
        ]);
        var state = await CreateStateAsync(handler);

        var cut = Render<AdminAudit>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForState(() => cut.FindAll("[data-testid='audit-entry']").Count == 1);
        cut.Find("[data-testid='load-older-audit']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='audit-entry']").Count == 2);

        Assert.Equal(
            ("newer|older", "/api/admin/audit?before=11"),
            (string.Join('|', cut.FindAll("[data-testid='audit-action']").Select(element => element.TextContent.Trim())), handler.RequestPaths.Last()));
    }

    [Fact]
    public async Task Moderator_cannot_read_the_audit_screen_or_see_its_navigation_entry()
    {
        var handler = new QueueHandler([SessionResponse("Moderator")]);
        var state = await CreateStateAsync(handler);
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new AdminAuditClientEntry().Register(registry);

        var cut = Render<AdminAudit>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForState(() => cut.Markup.Contains("admin.audit.denied", StringComparison.Ordinal));

        Assert.Equal((true, false, 1),
            (cut.Markup.Contains("admin.audit.denied", StringComparison.Ordinal), registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false)), handler.RequestPaths.Count));
    }

    private async Task<AdminState> CreateStateAsync(QueueHandler handler)
    {
        Services.AddSingleton<IClientTranslator>(new PassthroughTranslator());
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private static HttpResponseMessage SessionResponse(string role) =>
        JsonResponse($$"""{"id":"33333333-3333-3333-3333-333333333333","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""");

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class QueueHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class PassthroughTranslator : IClientTranslator
    {
        public string this[string key] => key;
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
}
