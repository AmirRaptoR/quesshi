using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quesshi.Kinds.MapPick.Client;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PackAdminPageTests : BunitContext
{
    private static readonly Guid Pack = Guid.Parse("b2b4a9a0-5f0c-4c1e-8c4e-0b8a6d0c0001");
    private static readonly Guid Choice = Guid.Parse("b2b4a9a0-5f0c-4c1e-8c4e-0b8a6d0c0002");
    private static readonly Guid Order = Guid.Parse("b2b4a9a0-5f0c-4c1e-8c4e-0b8a6d0c0003");

    public PackAdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
        Services.AddSingleton<IJSRuntime>(new PlainJs());
        var registry = new ClientRegistry(new NoopLoader());
        new WebPluginsEntry().Register(registry);
        new MapPickClientEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
    }

    [Fact]
    public async Task Packs_page_renders_the_pack_list_and_a_kind_editor_per_question_of_the_open_pack()
    {
        var state = await StateAsync("Editor");
        var cut = Render<AdminPacks>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForAssertion(() => Assert.Equal([Pack.ToString()], cut.FindAll("[data-testid=pack-row]").Select(row => row.GetAttribute("data-pack-id"))));
        cut.Find("[data-testid=edit-pack]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=pack-question]").Count));
        var questions = cut.FindAll("[data-testid=pack-question]");
        Assert.Equal([Choice.ToString(), Order.ToString()], questions.Select(question => question.GetAttribute("data-question-id")));
        Assert.Single(questions[0].QuerySelectorAll("[data-kind=choice]"));
        Assert.Single(questions[1].QuerySelectorAll("[data-kind=order]"));
        Assert.Equal("Capitals", cut.Find("[data-testid=pack-title]").GetAttribute("value"));
    }

    [Fact]
    public async Task Packs_page_refuses_a_moderator_without_loading_packs()
    {
        var state = await StateAsync("Moderator");
        var cut = Render<AdminPacks>(parameters => parameters.AddCascadingValue(state));

        Assert.Empty(cut.FindAll("[data-testid=pack-screen]"));
        Assert.Contains("admin.packs.denied", cut.Markup);
    }

    private async Task<AdminState> StateAsync(string role)
    {
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Json($$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}"""),
            "/api/config" => Json("""{"languages":[{"code":"en","displayName":"English","direction":"ltr"}],"defaultLanguage":"en","capabilities":{}}"""),
            "/api/admin/packs" => Json($$"""[{"id":"{{Pack}}","title":"Capitals","description":null,"language":"en","visibility":"private"}]"""),
            var path when path == $"/api/admin/packs/{Pack}" => Json($$"""
                {"pack":{"id":"{{Pack}}","title":"Capitals","description":null,"language":"en","visibility":"private"},
                 "questions":[
                   {"position":0,"id":"{{Choice}}","kind":"choice","kindVersion":1,"family":"trivia","language":"en","categoryId":null,"level":1,"prompt":"Capital of France?","payload":{"options":["Paris","Lyon","Nice","Lille"],"correctIndex":0},"explanation":null,"status":"approved","mediaId":null},
                   {"position":1,"id":"{{Order}}","kind":"order","kindVersion":1,"family":"trivia","language":"en","categoryId":null,"level":1,"prompt":"Order these","payload":{"items":[{"id":"one","text":"One"},{"id":"two","text":"Two"}]},"explanation":null,"status":"approved","mediaId":null}]}
                """),
            var path => throw new InvalidOperationException($"Unexpected request {path}."),
        });
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class PlainJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "quesshi.get") return ValueTask.FromResult((TValue)(object)"en");
            if (identifier == "quesshi.browserLanguages") return ValueTask.FromResult((TValue)(object)new[] { "en" });
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class Translator : IClientTranslator
    {
        public string this[string key] => key;
    }

    private sealed class NoopLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string _) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }
}
