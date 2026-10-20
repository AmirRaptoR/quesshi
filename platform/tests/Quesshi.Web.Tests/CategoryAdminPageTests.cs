using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class CategoryAdminPageTests : BunitContext
{
    private static readonly Guid CategoryId = Guid.Parse("83128a71-f3c3-4cbb-90bf-e20ba9904252");

    public CategoryAdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
    }

    [Fact]
    public async Task Editor_category_screen_uses_the_selected_RTL_language_and_saves_the_bank_policy()
    {
        UseLanguage("fa", ["fa-IR"]);
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, ConfigJson("fa", "fa", "rtl")),
            Response(HttpStatusCode.OK, CategoriesJson()),
            Response(HttpStatusCode.OK, """{"categoriesByFamily":{"trivia":[],"poll":[]}}"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.NoContent),
            Response(HttpStatusCode.OK, ConfigJson("fa", "fa", "rtl")),
            Response(HttpStatusCode.OK, CategoriesJson()),
            Response(HttpStatusCode.OK, """{"categoriesByFamily":{"trivia":["83128a71-f3c3-4cbb-90bf-e20ba9904252"],"poll":[]}}"""));
        var state = await StateAsync(handler);

        var cut = Render<AdminCategories>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.Find("section.stack").GetAttribute("lang") == "fa");
        cut.Find("fieldset input[type=checkbox]").Change(true);
        cut.Find("button[type=button]").Click();
        cut.WaitForState(() => handler.Paths.Contains("/api/admin/categories/bank-settings", StringComparer.Ordinal));

        Assert.Equal(("fa", "rtl", true),
            (cut.Find("section.stack").GetAttribute("lang"), cut.Find("section.stack").GetAttribute("dir"),
                handler.Bodies.Single(body => body.Contains("categoriesByFamily", StringComparison.Ordinal)).Contains(CategoryId.ToString(), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Owner_category_screen_creates_an_English_category()
    {
        UseLanguage(null, ["en-US"]);
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Owner")),
            Response(HttpStatusCode.OK, ConfigJson("en", "en", "ltr")),
            Response(HttpStatusCode.OK, "[]"),
            Response(HttpStatusCode.OK, """{"categoriesByFamily":{"trivia":[],"poll":[]}}"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, CategoryJson()),
            Response(HttpStatusCode.OK, ConfigJson("en", "en", "ltr")),
            Response(HttpStatusCode.OK, CategoriesJson()),
            Response(HttpStatusCode.OK, """{"categoriesByFamily":{"trivia":[],"poll":[]}}"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminCategories>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.Find("section.stack").GetAttribute("lang") == "en");
        cut.FindAll("form")[0].QuerySelectorAll("input")[0].Change("Science");
        cut.FindAll("form")[0].QuerySelectorAll("input")[1].Change("science");
        cut.FindAll("form")[0].Submit();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Post, "/api/admin/categories")));

        Assert.Equal(("en", true),
            (cut.Find("section.stack").GetAttribute("lang"), handler.Bodies.Single(body => body.Contains("science", StringComparison.Ordinal)).Contains("Science", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Editor_category_screen_shows_a_bank_policy_validation_error()
    {
        UseLanguage(null, ["en-US"]);
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, ConfigJson("en", "en", "ltr")),
            Response(HttpStatusCode.OK, CategoriesJson()),
            Response(HttpStatusCode.OK, """{"categoriesByFamily":{"trivia":[],"poll":[]}}"""),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_bank_category_settings"}"""));
        var state = await StateAsync(handler);
        var cut = Render<AdminCategories>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("fieldset").Count > 0);
        cut.Find("button[type=button]").Click();

        cut.WaitForState(() => cut.FindAll("[role=alert]").Count == 1);
        Assert.Single(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task Moderator_cannot_open_category_controls_and_category_navigation_excludes_the_role()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Moderator")));
        var state = await StateAsync(handler);
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new CategoryAdminClientEntry().Register(registry);

        var cut = Render<AdminCategories>(parameters => parameters.AddCascadingValue(state));

        Assert.Equal((0, false), (cut.FindAll("form").Count, registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false))));
    }

    private void UseLanguage(string? saved, string[] browserLanguages) => Services.AddSingleton<IJSRuntime>(new LanguageJs(saved, browserLanguages));

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private static string SessionJson(string role) => $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";
    private static string ConfigJson(string defaultLanguage, string code, string direction) => $$"""{"languages":[{"code":"{{code}}","displayName":"{{code}}","direction":"{{direction}}"}],"defaultLanguage":"{{defaultLanguage}}"}""";
    private static string CategoriesJson() => $$"""[{{CategoryJson()}}]""";
    private static string CategoryJson() => $$"""{"id":"{{CategoryId}}","slug":"science","names":{"en":"Science","fa":"دانش"},"icon":"flask","color":"blue","sortOrder":2,"isActive":true}""";
    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status) { Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> Paths => Requests.Select(request => request.Path).ToList();
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }

    private sealed class LanguageJs(string? saved, string[] browserLanguages) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? value = identifier switch
            {
                "quesshi.get" => saved,
                "quesshi.browserLanguages" => browserLanguages,
                _ => throw new JSException($"Unexpected JavaScript call: {identifier}")
            };
            return ValueTask.FromResult((TValue)value!);
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
