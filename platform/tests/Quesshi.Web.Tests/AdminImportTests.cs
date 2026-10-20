using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class AdminImportTests : BunitContext
{
    public AdminImportTests() => Services.AddSingleton<IClientTranslator>(new TestTranslator());

    [Fact]
    public async Task Dry_run_renders_every_row_outcome_and_problem_without_enabling_import_for_only_failures()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"invalid","problems":[{"code":"prompt.required","message":"A prompt is required."}]},{"row":3,"outcome":"duplicate","existingQuestionId":"e2719a53-c9d6-4f18-af24-dad6ef71917f"},{"row":4,"outcome":"media_fetch_failed","problems":[{"code":"media.invalid","message":"The row media could not be reached."}]}],"accepted":0,"rejected":3,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "invalid.csv");
        cut.Find("[data-testid='dry-run']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("prompt.required: A prompt is required.", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("e2719a53-c9d6-4f18-af24-dad6ef71917f", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("media.invalid: The row media could not be reached.", cut.Markup, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled"));
        });

        var importRequest = Assert.Single(handler.Requests, request => request.Body is not null);
        Assert.Contains("dryRun\r\n\r\ntrue", importRequest.Body, StringComparison.Ordinal);
        Assert.Equal("csrf", importRequest.CsrfToken);
    }

    [Fact]
    public async Task Import_becomes_available_only_after_a_current_dry_run_with_importable_rows()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"}],"accepted":1,"rejected":0,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled"));
        cut.Find("[data-testid='dry-run']").Click();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("[data-testid='import']").GetAttribute("disabled")));

        cut.FindComponents<InputFile>()[1].UploadFiles(InputFileContent.CreateFromBinary([1, 2], "media.zip", contentType: "application/zip"));

        Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled"));
    }

    [Fact]
    public async Task Owner_can_dry_run_a_json_file_for_the_selected_kind()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"}],"accepted":1,"rejected":0,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Owner"));

        cut.Find("[data-testid='import-kind']").Change("poll_choice");
        cut.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromText("[]", "questions.json", contentType: "application/json"));
        cut.Find("[data-testid='dry-run']").Click();

        cut.WaitForAssertion(() => Assert.Null(cut.Find("[data-testid='import']").GetAttribute("disabled")));
    }

    [Fact]
    public async Task Dry_run_submits_the_selected_optional_media_bundle()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"}],"accepted":1,"rejected":0,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        cut.FindComponents<InputFile>()[1].UploadFiles(InputFileContent.CreateFromBinary([1, 2], "media.zip", contentType: "application/zip"));
        cut.Find("[data-testid='dry-run']").Click();

        cut.WaitForAssertion(() => Assert.Contains("name=mediaBundle", handler.Requests.Single(request => request.Body is not null).Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_the_question_kind_invalidates_a_successful_dry_run()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"}],"accepted":1,"rejected":0,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        cut.Find("[data-testid='dry-run']").Click();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("[data-testid='import']").GetAttribute("disabled")));
        cut.Find("[data-testid='import-kind']").Change("order");

        Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled"));
    }

    [Fact]
    public async Task Changing_the_question_file_invalidates_a_successful_dry_run()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"}],"accepted":1,"rejected":0,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "first.csv");
        cut.Find("[data-testid='dry-run']").Click();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("[data-testid='import']").GetAttribute("disabled")));
        UploadQuestionFile(cut, "second.csv");

        Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled"));
    }

    [Fact]
    public async Task A_failed_dry_run_keeps_import_disabled()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_import"}""", Encoding.UTF8, "application/json") });
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        cut.Find("[data-testid='dry-run']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("admin.import.failed", cut.Markup, StringComparison.Ordinal);
            Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled"));
        });
    }

    [Fact]
    public async Task A_dry_run_with_no_importable_rows_keeps_import_disabled()
    {
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"invalid","problems":[{"code":"prompt.required","message":"A prompt is required."}]}],"accepted":0,"rejected":1,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        cut.Find("[data-testid='dry-run']").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='import']").GetAttribute("disabled")));
    }

    [Fact]
    public async Task Import_replaces_the_dry_run_with_imported_and_failed_row_results()
    {
        var questionId = "c4c4ca7d-0b48-4a09-b42f-3bd14d9e1f8a";
        var handler = new QueueHandler(
            Json("""{"token":"csrf"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"},{"row":3,"outcome":"would_import"}],"accepted":2,"rejected":0,"warnings":[]}"""),
            Json($$"""{"rows":[{"row":2,"outcome":"imported","questionId":"{{questionId}}"},{"row":3,"outcome":"invalid","problems":[{"code":"question.invalid","message":"The question could not be imported."}]}],"accepted":1,"rejected":1,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        cut.Find("[data-testid='dry-run']").Click();
        cut.WaitForAssertion(() => Assert.Null(cut.Find("[data-testid='import']").GetAttribute("disabled")));
        cut.Find("[data-testid='import']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(questionId, cut.Markup, StringComparison.Ordinal);
            Assert.Contains("question.invalid: The question could not be imported.", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Contains("dryRun\r\n\r\nfalse", handler.Requests.Last().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_retries_once_with_a_fresh_antiforgery_token()
    {
        var handler = new QueueHandler(
            Json("""{"token":"expired"}"""),
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_antiforgery"}""", Encoding.UTF8, "application/json") },
            Json("""{"token":"fresh"}"""),
            Json("""{"rows":[{"row":2,"outcome":"would_import"}],"accepted":1,"rejected":0,"warnings":[]}"""));
        var cut = RenderImport(handler, await SignedInStateAsync("Editor"));

        UploadQuestionFile(cut, "questions.csv");
        cut.Find("[data-testid='dry-run']").Click();

        cut.WaitForAssertion(() => Assert.Null(cut.Find("[data-testid='import']").GetAttribute("disabled")));
        var imports = handler.Requests.Where(request => request.Body is not null).ToArray();
        Assert.Equal(["expired", "fresh"], imports.Select(request => request.CsrfToken));
    }

    [Fact]
    public async Task Editor_sees_csv_and_json_templates_for_every_supported_kind()
    {
        var cut = RenderImport(new QueueHandler(), await SignedInStateAsync("Editor"));

        foreach (var kind in new[] { "choice", "order", "map_pick", "poll_choice", "poll_roster" })
        {
            Assert.NotNull(cut.Find($"[data-testid='template-{kind}'] a[href='/api/admin/import/templates/{kind}.csv']"));
            Assert.NotNull(cut.Find($"[data-testid='template-{kind}'] a[href='/api/admin/import/templates/{kind}.json']"));
        }
    }

    [Fact]
    public async Task Moderator_cannot_see_import_controls_or_templates()
    {
        var cut = RenderImport(new QueueHandler(), await SignedInStateAsync("Moderator"));

        Assert.Contains("admin.import.denied", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input, [data-testid^='template-']"));
    }

    [Fact]
    public async Task Signed_out_admin_is_redirected_without_import_controls_or_templates()
    {
        var cut = RenderImport(new QueueHandler(), await SignedOutStateAsync());

        Assert.EndsWith("/admin/login", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input, [data-testid^='template-']"));
    }

    private IRenderedComponent<AdminImport> RenderImport(QueueHandler handler, AdminState state)
    {
        Services.AddSingleton(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NoopReloader()));
        return Render<AdminImport>(parameters => parameters.AddCascadingValue(state));
    }

    private static void UploadQuestionFile(IRenderedComponent<AdminImport> cut, string name) =>
        cut.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromText("prompt,choice1,choice2,choice3,choice4,correctIndex\nWhat?,a,b,c,d,0", name, contentType: "text/csv"));

    private static async Task<AdminState> SignedInStateAsync(string role)
    {
        var handler = new QueueHandler(Json($$"""{"id":"d6f40d9b-4c49-46ed-8613-8ec924e5a203","role":"{{role}}","isPlatform":false,"mustChangePassword":false}"""));
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NoopReloader())));
        await state.InitializeAsync();
        return state;
    }

    private static async Task<AdminState> SignedOutStateAsync()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NoopReloader())));
        await state.InitializeAsync();
        return state;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("X-CSRF-Admin", out var csrf);
            Requests.Add(new RecordedRequest(body, csrf?.SingleOrDefault()));
            return responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(string? Body, string? CsrfToken);
    private sealed class TestTranslator : IClientTranslator { public string this[string key] => key; }
    private sealed class NoopReloader : IClientReloader { public Task ReloadOnceAsync(string buildId) => Task.CompletedTask; }
}
