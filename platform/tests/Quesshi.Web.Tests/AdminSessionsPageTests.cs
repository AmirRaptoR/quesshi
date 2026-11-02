using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AdminSessionsPageTests : BunitContext
{
    private const string SessionId = "5e0c4b57-6c2c-4d52-9a0e-0d0a6b0c1f11";

    public AdminSessionsPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
    }

    [Fact]
    public async Task Moderator_screen_lists_the_active_sessions_and_ends_one_through_the_admin_endpoint()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Moderator")),
            Response(HttpStatusCode.OK, $$"""[{"id":"{{SessionId}}","code":"ABCD23","format":"live","status":"running","createdAt":"2026-10-22T12:00:00+00:00","players":3}]"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, """{"ended":true}"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminSessions>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll($"[data-testid=end-session-{SessionId}]").Count == 1);
        Assert.Contains("ABCD23", cut.Find($"[data-testid=admin-session-{SessionId}]").TextContent, StringComparison.Ordinal);
        cut.Find($"[data-testid=end-session-{SessionId}]").Click();
        cut.WaitForState(() => cut.FindAll($"[data-testid=admin-session-{SessionId}]").Count == 0);

        Assert.Contains((HttpMethod.Post, $"/api/admin/session-operations/{SessionId}/end"), handler.Requests);
    }

    [Fact]
    public async Task Editor_cannot_open_session_operations_and_navigation_excludes_the_role()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Editor")));
        var state = await StateAsync(handler);
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new AdminSessionsClientEntry().Register(registry);

        var cut = Render<AdminSessions>(parameters => parameters.AddCascadingValue(state));

        Assert.Equal((0, false, true), (cut.FindAll("button").Count, registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Editor", false)),
            registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false))));
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
