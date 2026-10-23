using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quesshi.Kinds.MapPick.Client;
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
        new MapPickClientEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
    }

    [Fact]
    public async Task Editor_question_screen_shows_usage_from_the_enabled_stats_feature()
    {
        var question = Guid.Parse("2818af63-3f8d-4b09-8adb-47b55914ea6d");
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/config" => Response(HttpStatusCode.OK, ConfigJson("{\"feature\":[\"stats\"]}")),
            "/api/admin/categories" => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/categories?family=trivia", StringComparison.Ordinal) => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/admin/questions?", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("trivia", question)),
            var path when path == $"/api/admin/x/stats/questions?ids={question}" => Response(HttpStatusCode.OK, QuestionUsageJson(question, 8, 4, .75)),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);

        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("[data-testid=question-usage]").Count == 1);
        Assert.Equal(("8", "4", "75 %"), (
            cut.Find("[data-testid=question-served]").TextContent,
            cut.Find("[data-testid=question-asked]").TextContent,
            cut.Find("[data-testid=question-accuracy]").TextContent));
        Assert.Contains($"/api/admin/x/stats/questions?ids={question}", handler.Paths);
    }

    [Fact]
    public async Task Editor_question_screen_shows_empty_accuracy_when_no_answers_were_asked()
    {
        var question = Guid.Parse("3818d822-6b1e-4a64-9a6c-68c6d8c3f7b2");
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/config" => Response(HttpStatusCode.OK, ConfigJson("""{"feature":["stats"]}""")),
            "/api/admin/categories" => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/categories?family=trivia", StringComparison.Ordinal) => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/admin/questions?", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("trivia", question)),
            var path when path == $"/api/admin/x/stats/questions?ids={question}" => Response(HttpStatusCode.OK, QuestionUsageJson(question, 7, 0, 0)),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);
        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForAssertion(() => Assert.Equal("—", cut.Find("[data-testid=question-accuracy]").TextContent));
    }

    [Fact]
    public async Task Editor_question_screen_omits_usage_and_stats_request_when_the_feature_is_disabled()
    {
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/config" => Response(HttpStatusCode.OK, ConfigJson()),
            "/api/admin/categories" => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/categories?family=trivia", StringComparison.Ordinal) => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/admin/questions?", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("trivia")),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);

        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForState(() => cut.FindAll("[data-testid=question-row]").Count == 1);
        Assert.Empty(cut.FindAll("[data-testid=question-usage]"));
        Assert.DoesNotContain(handler.Paths, path => path.StartsWith("/api/admin/x/stats/questions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editor_question_screen_refreshes_usage_for_the_selected_family()
    {
        var triviaQuestion = Guid.Parse("d67c64a8-a6a1-4d8e-839f-e48f584a7b28");
        var pollQuestion = Guid.Parse("2dfad67d-15b3-4bce-82d5-c9dca2ff486a");
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/config" => Response(HttpStatusCode.OK, ConfigJson("""{"feature":["stats"]}""")),
            "/api/admin/categories" => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/categories?family=", StringComparison.Ordinal) => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/admin/questions?", StringComparison.Ordinal) && path.Contains("family=poll", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("poll", pollQuestion)),
            var path when path.StartsWith("/api/admin/questions?", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("trivia", triviaQuestion)),
            var path when path == $"/api/admin/x/stats/questions?ids={triviaQuestion}" => Response(HttpStatusCode.OK, QuestionUsageJson(triviaQuestion, 2, 1, 1)),
            var path when path == $"/api/admin/x/stats/questions?ids={pollQuestion}" => Response(HttpStatusCode.OK, QuestionUsageJson(pollQuestion, 6, 3, .5)),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);
        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid=question-served]").TextContent));
        cut.Find("[data-testid=question-family]").Change("poll");
        cut.WaitForAssertion(() => Assert.Equal(("poll", "6", "3", "50 %"), (
            cut.Find("[data-testid=question-row]").GetAttribute("data-family"),
            cut.Find("[data-testid=question-served]").TextContent,
            cut.Find("[data-testid=question-asked]").TextContent,
            cut.Find("[data-testid=question-accuracy]").TextContent)));
        Assert.Contains($"/api/admin/x/stats/questions?ids={pollQuestion}", handler.Paths);
    }

    [Fact]
    public async Task Editor_question_screen_filters_the_shared_poll_list_in_the_selected_RTL_language()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, ConfigJson()),
            Response(HttpStatusCode.OK, "[]"),
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
        cut.Find("form").Submit();

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

    [Fact]
    public async Task Editor_refreshes_categories_for_the_selected_question_family()
    {
        var triviaCategory = Guid.Parse("9c779d88-f386-4333-9409-2fdc0de4c6c7");
        var pollCategory = Guid.Parse("e8cfbffb-c12c-4b99-a53e-f929e3b5a97f");
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/config" => Response(HttpStatusCode.OK, ConfigJson()),
            "/api/admin/categories" => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/categories?family=trivia", StringComparison.Ordinal) => Response(HttpStatusCode.OK, $$"""[{"id":"{{triviaCategory}}","slug":"trivia"}]"""),
            var path when path.StartsWith("/api/categories?family=poll", StringComparison.Ordinal) => Response(HttpStatusCode.OK, $$"""[{"id":"{{pollCategory}}","slug":"poll"}]"""),
            var path when path.StartsWith("/api/admin/questions", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("trivia")),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);

        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForState(() => cut.FindAll("[data-testid=question-row]").Count == 1);
        cut.Find("[data-testid=new-question]").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid=question-editor]").Count == 1);
        cut.Find("[data-testid=question-editor-kind]").Change("poll_choice");
        cut.WaitForState(() => cut.FindAll("[data-testid=question-editor] option").Any(option => option.TextContent == "poll"));

        Assert.DoesNotContain(cut.FindAll("[data-testid=question-editor] option"), option => option.TextContent == "trivia");
    }

    [Fact]
    public async Task Existing_question_preview_does_not_mutate_the_question()
    {
        var question = Guid.Parse("ee26c619-a53a-4379-9724-c75c83c16eb5");
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/config" => Response(HttpStatusCode.OK, ConfigJson()),
            "/api/admin/categories" => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/categories?family=trivia", StringComparison.Ordinal) => Response(HttpStatusCode.OK, "[]"),
            var path when path.StartsWith("/api/admin/questions?", StringComparison.Ordinal) => Response(HttpStatusCode.OK, QuestionListJson("trivia", question, "choice")),
            var path when path == $"/api/admin/questions/{question}/preview" => Response(HttpStatusCode.OK, PreviewJson(question, "choice", "{\"options\":[\"A\",\"B\",\"C\",\"D\"]}")),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);

        var cut = Render<AdminQuestions>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForState(() => cut.FindAll("[data-testid=preview-question]").Count == 1);
        cut.Find("[data-testid=preview-question]").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid=question-preview]").Count == 1);

        Assert.DoesNotContain(handler.Methods, method => method is "POST" or "PUT" or "DELETE");
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("order")]
    [InlineData("map_pick")]
    [InlineData("poll_choice")]
    [InlineData("poll_roster")]
    public void Each_kind_editor_submits_and_reloads_the_saved_question(string kind)
    {
        var question = Guid.Parse("0fcce902-aefc-4161-bf43-6bdf832a7657");
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/antiforgery" => Response(HttpStatusCode.OK, """{"token":"test-token"}"""),
            "/api/admin/questions" => Response(HttpStatusCode.OK, QuestionJson(question, kind, "Created prompt")),
            var path when path == $"/api/admin/questions/{question}" => Response(HttpStatusCode.OK, QuestionJson(question, kind, "Edited prompt")),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        Services.AddSingleton(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
        QuestionResponse? saved = null;

        var cut = RenderEditor(EventCallback.Factory.Create<QuestionResponse>(this, value => saved = value));
        ChooseKindAndEditPayload(cut, kind);
        cut.Find("[data-testid=question-category]").Change(Categories[0].Id.ToString());
        cut.Find("[data-testid=question-level]").Change(2);
        cut.Find("[data-testid=question-prompt]").Change("Created prompt");
        cut.Find("[data-testid=question-explanation]").Change("Created explanation");
        cut.Find("[data-testid=question-status]").Change("approved");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.NotNull(saved));

        var reloaded = RenderEditor(EventCallback.Factory.Create<QuestionResponse>(this, value => saved = value), saved);
        Assert.Equal("Created prompt", reloaded.Find("[data-testid=question-prompt]").GetAttribute("value"));
        Assert.Equal(Categories[0].Id.ToString(), reloaded.Find("[data-testid=question-category]").GetAttribute("value"));
        Assert.Equal("2", reloaded.Find("[data-testid=question-level]").GetAttribute("value"));
        Assert.Equal("Saved explanation", reloaded.Find("[data-testid=question-explanation]").GetAttribute("value"));
        Assert.Equal("approved", reloaded.Find("[data-testid=question-status]").GetAttribute("value"));
        reloaded.Find("[data-testid=question-prompt]").Change("Edited prompt");
        reloaded.Find("form").Submit();
        reloaded.WaitForAssertion(() => Assert.Equal(2, handler.Paths.Count(path => path.Contains("/api/admin/questions", StringComparison.Ordinal))));

        using var createdRequest = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(kind, createdRequest.RootElement.GetProperty("kind").GetString());
        Assert.Equal(Categories[0].Id.ToString(), createdRequest.RootElement.GetProperty("categoryId").GetString());
        Assert.Equal(2, createdRequest.RootElement.GetProperty("level").GetInt32());
        Assert.Equal("Created prompt", createdRequest.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("Created explanation", createdRequest.RootElement.GetProperty("explanation").GetString());
        Assert.Equal("approved", createdRequest.RootElement.GetProperty("status").GetString());
        Assert.True(createdRequest.RootElement.TryGetProperty("payload", out _));

        using var editedRequest = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("Edited prompt", editedRequest.RootElement.GetProperty("prompt").GetString());
    }

    [Theory]
    [InlineData("choice", "options.tooFew", "Choice needs four options.")]
    [InlineData("order", "items.invalid", "Order needs valid items.")]
    [InlineData("map_pick", "target.invalid", "Map target is invalid.")]
    [InlineData("poll_choice", "options.tooFew", "Poll needs two options.")]
    [InlineData("poll_roster", "roster.invalid", "Roster is unavailable.")]
    public async Task Each_kind_keeps_its_draft_when_envelope_and_kind_validation_fail(string kind, string code, string kindMessage)
    {
        var handler = new Handler(request => request.RequestUri!.PathAndQuery switch
        {
            "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson("Editor")),
            "/api/admin/antiforgery" => Response(HttpStatusCode.OK, """{"token":"test-token"}"""),
            "/api/admin/questions" => Response(HttpStatusCode.BadRequest, $$"""{"error":"invalid_question","problems":[{"code":"question.invalid","message":"The question envelope is incomplete."},{"code":"{{code}}","message":"{{kindMessage}}"},{"code":"media.unknown","message":"The selected medium does not exist."}]}"""),
            var path => throw new InvalidOperationException($"Unexpected request {path}.")
        });
        var state = await StateAsync(handler);
        var cut = Render<QuestionEditor>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.Languages, Languages)
            .Add(component => component.Categories, Categories)
            .Add(component => component.Saved, EventCallback.Factory.Create<QuestionResponse>(this, _ => { })));
        ChooseKindAndEditPayload(cut, kind);
        cut.Find("[data-testid=question-prompt]").Change($"Retained {kind}");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(kindMessage, cut.FindAll("[data-testid=question-problem]").Select(element => element.TextContent)));
        Assert.Equal($"Retained {kind}", cut.Find("[data-testid=question-prompt]").GetAttribute("value"));
        Assert.Equal("The selected medium does not exist.", cut.Find("[data-testid=media-problem]").TextContent);
    }

    [Theory]
    [InlineData("choice", "{\"options\":[\"A\",\"B\",\"C\",\"D\"]}")]
    [InlineData("order", "{\"items\":[{\"id\":\"one\",\"text\":\"One\"},{\"id\":\"two\",\"text\":\"Two\"}]}")]
    [InlineData("map_pick", "{\"baseLayer\":\"Borders\",\"shape\":\"Country\"}")]
    [InlineData("poll_choice", "{\"options\":[{\"id\":\"one\",\"text\":\"One\"},{\"id\":\"two\",\"text\":\"Two\"}]}")]
    [InlineData("poll_roster", "{\"options\":[{\"id\":\"one\",\"text\":\"One\"}]}")]
    public async Task Preview_renders_the_real_player_card_for_each_kind(string kind, string card)
    {
        var question = Guid.Parse("e8af925a-1360-41c3-b56a-22c93bbb5f08");
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Editor")), Response(HttpStatusCode.OK, PreviewJson(question, kind, card)));
        var state = await StateAsync(handler);

        var cut = Render<QuestionPreview>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.QuestionId, question)
            .Add(component => component.Language, "en")
            .Add(component => component.Direction, "ltr"));

        cut.WaitForState(() => cut.FindAll($"[data-kind={kind}]").Count == 1);
        Assert.Empty(cut.FindAll("[data-correct-option]"));
    }

    [Fact]
    public void Shared_editor_preserves_map_and_poll_transition_drafts()
    {
        var handler = new Handler();
        Services.AddSingleton(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader()));
        var mapQuestion = ExistingQuestion("map_pick", "{\"baseLayer\":\"Blank\",\"target\":{\"shape\":\"Point\",\"latitude\":35.7,\"longitude\":51.4,\"radiusKm\":10}}");
        var cut = RenderEditor(EventCallback.Factory.Create<QuestionResponse>(this, _ => { }), mapQuestion);

        cut.Find("[data-testid=map-base-layer]").Change("Borders");
        cut.Find("[data-testid=map-target-shape]").Change("Country");
        cut.Find("[data-testid=map-country-code]").Change("NL");
        cut.Find("[data-testid=question-editor-kind]").Change("poll_choice");
        cut.WaitForState(() => cut.FindAll("[data-kind=poll_choice]").Count == 1);
        cut.Find("[data-kind=poll_choice] input[data-option-text='0']").Input("Retained poll choice");
        cut.Find("[data-kind=poll_choice] [data-payload-save]").Click();
        for (var index = 0; index < 6; index++) cut.Find("[data-kind=poll_choice] [data-add-option]").Click();
        cut.Find("[data-kind=poll_choice] [data-payload-save]").Click();
        cut.Find("[data-testid=question-editor-kind]").Change("poll_roster");
        cut.WaitForState(() => cut.FindAll("[data-kind=poll_roster]").Count == 1);
        Assert.Empty(cut.FindAll("[data-kind=poll_roster] input"));
        cut.Find("[data-testid=question-editor-kind]").Change("poll_choice");
        cut.WaitForState(() => cut.FindAll("[data-kind=poll_choice]").Count == 1);

        Assert.Equal("Retained poll choice", cut.Find("[data-kind=poll_choice] input[data-option-text='0']").GetAttribute("value"));
        Assert.True(cut.Find("[data-kind=poll_choice] [data-add-option]").HasAttribute("disabled"));
        cut.Find("[data-testid=question-editor-kind]").Change("map_pick");
        cut.WaitForState(() => cut.FindAll("[data-kind=map_pick]").Count == 1);

        Assert.Equal(("Borders", "NL"), (
            cut.Find("[data-testid=map-base-layer]").GetAttribute("value"),
            cut.Find("[data-testid=map-country-code]").GetAttribute("value")));
    }

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return state;
    }

    private IRenderedComponent<QuestionEditor> RenderEditor(EventCallback<QuestionResponse> saved, QuestionResponse? question = null) =>
        Render<QuestionEditor>(parameters => parameters
            .Add(component => component.Question, question)
            .Add(component => component.Languages, Languages)
            .Add(component => component.Categories, Categories)
            .Add(component => component.Saved, saved));

    private static readonly IReadOnlyList<QuestionEditor.TenantLanguageModel> Languages =
        [new("en", "English", "ltr")];

    private static readonly IReadOnlyList<QuestionEditor.QuestionCategoryModel> Categories =
        [new(Guid.Parse("50e8f84b-99a8-4d16-9ae5-5b43a7d47e8f"), "history")];

    private static void ChooseKindAndEditPayload(IRenderedComponent<QuestionEditor> cut, string kind)
    {
        cut.Find("[data-testid=question-editor-kind]").Change(kind);
        cut.WaitForState(() => cut.FindAll($"[data-kind={kind}]").Count == 1);
        switch (kind)
        {
            case "choice":
                cut.Find("[data-kind=choice] input[data-option='0']").Input("Edited choice");
                break;
            case "order":
                cut.Find("[data-kind=order] input[data-item-text='0']").Input("Edited order");
                break;
            case "map_pick":
                cut.Find("[data-testid=map-target-shape]").Change("Country");
                cut.Find("[data-testid=map-country-code]").Change("NL");
                return;
            case "poll_choice":
                cut.Find("[data-kind=poll_choice] input[data-option-text='0']").Input("Edited poll choice");
                break;
            case "poll_roster":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        cut.Find($"[data-kind={kind}] [data-payload-save]").Click();
    }

    private static QuestionResponse ExistingQuestion(string kind, string payload) => new(
        Guid.Parse("f3aa6e18-c7db-4fad-bdbc-687c1aacb5f9"), kind, 1,
        kind.StartsWith("poll_", StringComparison.Ordinal) ? "poll" : "trivia", "en", Categories[0].Id, 1,
        "Existing prompt", JsonDocument.Parse(payload).RootElement.Clone(), null, null, "pending", "admin", null, null);

    private static string SessionJson(string role) => $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";
    private static string ConfigJson(string capabilities = "{}") => $$"""{"languages":[{"code":"fa","displayName":"فارسی","direction":"rtl"}],"defaultLanguage":"fa","capabilities":{{capabilities}}}""";
    private static string QuestionListJson(string family, Guid? id = null, string kind = "poll_choice") => $$"""{"items":[{"id":"{{id ?? Guid.Parse("2818af63-3f8d-4b09-8adb-47b55914ea6d")}}","kind":"{{kind}}","kindVersion":1,"family":"{{family}}","language":"fa","categoryId":null,"level":1,"prompt":"Question","payload":{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]},"explanation":null,"topic":null,"status":"pending","source":"admin","ownerId":null,"mediaId":null}],"total":1,"page":1,"pageSize":25,"buckets":[]}""";
    private static string QuestionUsageJson(Guid question, long served, long asked, double accuracy) => $$"""[{"questionId":"{{question}}","served":{{served}},"asked":{{asked}},"accuracy":{{accuracy}}}]""";
    private static string QuestionJson(Guid id, string kind, string prompt) => $$"""{"id":"{{id}}","kind":"{{kind}}","kindVersion":1,"family":"{{(kind.StartsWith("poll_", StringComparison.Ordinal) ? "poll" : "trivia")}}","language":"en","categoryId":"{{Categories[0].Id}}","level":2,"prompt":"{{prompt}}","payload":{{PayloadJson(kind)}},"explanation":"Saved explanation","topic":null,"status":"approved","source":"admin","ownerId":null,"mediaId":null}""";
    private static string PreviewJson(Guid id, string kind, string card) => $$"""{"id":"{{id}}","kind":"{{kind}}","kindVersion":1,"language":"en","prompt":"Preview","mediaId":null,"card":{{card}}}""";
    private static string PayloadJson(string kind) => kind switch
    {
        "choice" => "{\"options\":[\"A\",\"B\",\"C\",\"D\"],\"correctIndex\":0}",
        "order" => "{\"items\":[{\"id\":\"one\",\"text\":\"One\"},{\"id\":\"two\",\"text\":\"Two\"}]}",
        "map_pick" => "{\"baseLayer\":\"Borders\",\"target\":{\"shape\":\"Country\",\"countryCode\":\"NL\"}}",
        "poll_choice" => "{\"options\":[{\"id\":\"one\",\"text\":\"One\"},{\"id\":\"two\",\"text\":\"Two\"}]}",
        "poll_roster" => "{}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] queuedResponses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(queuedResponses);
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? responseFactory;
        public List<string> Paths { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string> Bodies { get; } = [];

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : this()
        {
            this.responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method.Method);
            if (request.Content is not null) Bodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(responseFactory is null ? responses.Dequeue() : responseFactory(request));
        }
    }

    private sealed class LanguageJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "quesshi.get") return ValueTask.FromResult((TValue)(object)"fa");
            if (identifier == "quesshi.browserLanguages") return ValueTask.FromResult((TValue)(object)new[] { "fa-IR" });
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
