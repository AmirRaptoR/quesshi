using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class QuestionAdminPageTests : BunitContext
{
    public QuestionAdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
        Services.AddSingleton<IJSRuntime>(new LanguageJs());
        var registry = new ClientRegistry(new NoopLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
    }

    [Fact]
    public async Task Editor_question_screen_filters_the_shared_poll_list_in_the_selected_RTL_language()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, ConfigJson()),
            Response(HttpStatusCode.OK, "[]"),
            Response(HttpStatusCode.OK, QuestionListJson("trivia")),
            Response(HttpStatusCode.OK, QuestionListJson("poll")));
        var state = await StateAsync(handler);

        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("[data-testid=question-row]").Count == 1);
        cut.Find("[data-testid=question-family]").Change("poll");
        cut.WaitForState(() => handler.Paths.Any(path => path.Contains("family=poll", StringComparison.Ordinal)));

        Assert.Equal(("fa", "rtl", "poll"),
            (cut.Find("[data-testid=question-screen]").GetAttribute("lang"),
                cut.Find("[data-testid=question-screen]").GetAttribute("dir"),
                cut.Find("[data-testid=question-row]").GetAttribute("data-family")));
    }

    [Fact]
    public async Task Moderator_cannot_open_question_editing_controls()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Moderator")));
        var state = await StateAsync(handler);

        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));

        Assert.Empty(cut.FindAll("[data-testid=question-editor]"));
    }

    [Fact]
    public void Invalid_question_problems_appear_at_the_relevant_fields_without_losing_the_draft()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, """{"token":"test-token"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_question","problems":[{"code":"question.invalid","message":"The question envelope is incomplete."},{"code":"options.tooFew","message":"Choice needs four options."}]}"""));
        Services.AddSingleton(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
        var category = Guid.Parse("50e8f84b-99a8-4d16-9ae5-5b43a7d47e8f");

        var cut = Render<QuestionEditor>(parameters => parameters
            .Add(component => component.Languages, [new QuestionEditor.TenantLanguageModel("fa", "فارسی", "rtl")])
            .Add(component => component.Categories, [new QuestionEditor.QuestionCategoryModel(category, "history")])
            .Add(component => component.Saved, EventCallback.Factory.Create<QuestionResponse>(this, _ => { })));
        cut.Find("[data-testid=question-prompt]").Change("A retained draft");
        cut.Find("[data-testid=save-question]").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            ["The question envelope is incomplete.", "Choice needs four options."],
            cut.FindAll("[data-testid=question-problem]").Select(element => element.TextContent)));
        Assert.Equal("A retained draft", cut.Find("[data-testid=question-prompt]").GetAttribute("value"));
    }

    [Fact]
    public void Question_editor_keeps_all_current_kinds_on_the_one_shared_form()
    {
        var handler = new Handler();
        Services.AddSingleton(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
        var category = Guid.Parse("50e8f84b-99a8-4d16-9ae5-5b43a7d47e8f");

        var cut = Render<QuestionEditor>(parameters => parameters
            .Add(component => component.Languages, [new QuestionEditor.TenantLanguageModel("en", "English", "ltr")])
            .Add(component => component.Categories, [new QuestionEditor.QuestionCategoryModel(category, "history")])
            .Add(component => component.Saved, EventCallback.Factory.Create<QuestionResponse>(this, _ => { })));

        Assert.Equal(["choice", "order", "map_pick", "poll_choice", "poll_roster"],
            cut.FindAll("[data-testid=question-editor-kind] option").Select(option => option.GetAttribute("value")));
    }

    [Fact]
    public void Question_editor_keeps_a_kind_draft_when_switching_away_and_back()
    {
        var handler = new Handler();
        Services.AddSingleton(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
        var category = Guid.Parse("50e8f84b-99a8-4d16-9ae5-5b43a7d47e8f");

        var cut = Render<QuestionEditor>(parameters => parameters
            .Add(component => component.Languages, [new QuestionEditor.TenantLanguageModel("en", "English", "ltr")])
            .Add(component => component.Categories, [new QuestionEditor.QuestionCategoryModel(category, "history")])
            .Add(component => component.Saved, EventCallback.Factory.Create<QuestionResponse>(this, _ => { })));
        cut.WaitForState(() => cut.FindAll("[data-kind=choice] input[data-option='0']").Count == 1);
        cut.Find("[data-kind=choice] input[data-option='0']").Input("A retained option");
        cut.Find("[data-kind=choice] [data-payload-save]").Click();
        cut.Find("[data-testid=question-editor-kind]").Change("order");
        cut.WaitForState(() => cut.FindAll("[data-kind=order]").Count == 1);
        cut.Find("[data-testid=question-editor-kind]").Change("choice");
        cut.WaitForState(() => cut.FindAll("[data-kind=choice] input[data-option='0']").Count == 1);

        Assert.Equal("A retained option", cut.Find("[data-kind=choice] input[data-option='0']").GetAttribute("value"));
    }

    [Fact]
    public async Task Preview_uses_the_player_choice_card_with_the_stored_medium_and_direction()
    {
        var question = Guid.Parse("ebf36b86-4a65-42c9-a614-c8b6e89aa9b8");
        var medium = Guid.Parse("3beb2b72-f34b-4ceb-b09b-b89799d9b679");
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, $$$"""{"id":"{{{question}}}","kind":"choice","kindVersion":1,"language":"fa","prompt":"پایتخت چیست؟","mediaId":"{{{medium}}}","card":{"options":["A","B","C","D"]}}"""),
            Response(HttpStatusCode.OK, $$$"""{"id":"{{{medium}}}","hash":"sample","kind":"image","mime":"image/png","bytes":8,"meta":{},"attribution":"Map source","url":"/media/sample.png"}"""));
        var state = await StateAsync(handler);

        var cut = Render<QuestionPreview>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.QuestionId, question)
            .Add(component => component.Language, "fa")
            .Add(component => component.Direction, "rtl"));

        cut.WaitForState(() => cut.FindAll("[data-testid=preview-medium] img").Count == 1);
        Assert.Equal(("fa", "rtl", "/media/sample.png", "A"), (
            cut.Find("[data-testid=question-preview]").GetAttribute("lang"),
            cut.Find("[data-testid=question-preview]").GetAttribute("dir"),
            cut.Find("[data-testid=preview-medium] img").GetAttribute("src"),
            cut.Find("[data-choice='0']").TextContent));
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
    private static string ConfigJson() => """{"languages":[{"code":"fa","displayName":"فارسی","direction":"rtl"}],"defaultLanguage":"fa"}""";
    private static string QuestionListJson(string family) => $$"""{"items":[{"id":"2818af63-3f8d-4b09-8adb-47b55914ea6d","kind":"poll_choice","kindVersion":1,"family":"{{family}}","language":"fa","categoryId":null,"level":1,"prompt":"Question","payload":{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]},"explanation":null,"topic":null,"status":"pending","source":"admin","ownerId":null,"mediaId":null}],"total":1,"page":1,"pageSize":25,"buckets":[]}""";
    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class LanguageJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)(identifier == "quesshi.get" ? "fa" : new[] { "fa-IR" }));
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
