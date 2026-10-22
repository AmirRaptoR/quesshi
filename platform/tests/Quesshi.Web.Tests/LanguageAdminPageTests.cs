using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class LanguageAdminPageTests : BunitContext
{
    public LanguageAdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
    }

    [Fact]
    public async Task Owner_language_screen_posts_a_new_language()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Owner")),
            Response(HttpStatusCode.OK, "[]"),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Created, """{"code":"fa","name":"فارسی","englishName":"Persian","direction":"rtl","isDefault":false}"""),
            Response(HttpStatusCode.OK, """[{"code":"fa","name":"فارسی","englishName":"Persian","direction":"rtl","isDefault":false}]"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminLanguages>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("form").Count == 1);
        cut.Find("#language-code").Change("fa");
        cut.Find("#language-local-name").Change("فارسی");
        cut.Find("#language-english-name").Change("Persian");
        cut.Find("#language-direction").Change("rtl");
        cut.Find("#language-create-form").Submit();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Post, "/api/admin/languages")));

        Assert.Contains("\"englishName\":\"Persian\"", handler.Bodies.Single(body => body.Contains("englishName", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moderator_cannot_open_language_controls_and_language_navigation_excludes_the_role()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Moderator")));
        var state = await StateAsync(handler);
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new LanguageAdminClientEntry().Register(registry);

        var cut = Render<AdminLanguages>(parameters => parameters.AddCascadingValue(state));

        Assert.Equal((0, false), (cut.FindAll("form").Count, registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false))));
    }

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private static string SessionJson(string role) => $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";
    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status) { Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
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
