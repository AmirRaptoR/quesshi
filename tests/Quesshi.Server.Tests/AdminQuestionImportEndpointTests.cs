using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// <c>POST /api/admin/questions/import</c> and <c>GET /api/admin/questions/import/template</c>, at
/// the real HTTP layer — the bulk counterpart to <c>AdminQuestionKindEndpointTests</c>, proving that
/// a row is validated through the exact same path the single-question form uses, that a dry run
/// writes nothing, and that the store's own <c>TopicKey</c> rule is enforced explicitly rather than
/// left to a unique index a dry run has nothing written yet to check.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class AdminQuestionImportEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"aqi-admin-{Guid.NewGuid():N}", "admin", "admin@example.com", "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private static string Csv(IEnumerable<string> header, params IEnumerable<string>[] rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header)).Append('\n');
        foreach (var row in rows) sb.Append(string.Join(',', row.Select(CsvField))).Append('\n');
        return sb.ToString();
    }

    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static readonly string[] ChoiceHeader =
        ["lang", "categoryId", "level", "prompt", "choice1", "choice2", "choice3", "choice4", "correctIndex",
            "explanation", "mediaUrl", "mediaKind", "status", "subject", "aspect"];

    private static string[] ChoiceRow(string prompt, string subject = "", string aspect = "", string status = "") =>
        ["en", "geography", "2", prompt, "Sargasso Sea", "Baltic Sea", "Red Sea", "Black Sea", "0",
            "", "", "", status, subject, aspect];

    private static readonly string[] SortHeader =
        ["lang", "categoryId", "level", "prompt", "item1", "item2", "item3", "item4",
            "explanation", "mediaUrl", "mediaKind", "status", "subject", "aspect"];

    private static string[] SortRow(string prompt) =>
        ["en", "geography", "2", prompt, "Tokyo", "Delhi", "Cairo", "Lima", "", "", "", "", "", ""];

    private static async Task<HttpResponseMessage> PostImportAsync(HttpClient client, string kind, string format,
        string body, bool dryRun = true)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(body, Encoding.UTF8), "file", $"import.{format}" }
        };
        return await client.PostAsync($"/api/admin/questions/import?kind={kind}&format={format}&dryRun={dryRun}", content);
    }

    private static async Task<ImportReportDto> ImportAsync(HttpClient client, string kind, string format, string body, bool dryRun = true)
    {
        var response = await PostImportAsync(client, kind, format, body, dryRun);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImportReportDto>())!;
    }

    // --- validation reuses the same codes -------------------------------------------

    [Fact]
    public async Task A_valid_choice_row_is_accepted()
    {
        using var client = AdminClient();
        var prompt = $"Which sea has no coastline {Guid.NewGuid():N}?";

        var report = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, ChoiceRow(prompt)));

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Accepted);
        Assert.Equal(0, report.Rejected);
        Assert.True(report.Rows.Single().Accepted);
        Assert.Null(report.Rows.Single().Error);
    }

    [Fact]
    public async Task A_choice_row_with_a_blank_choice_is_rejected_with_the_forms_own_code()
    {
        using var client = AdminClient();
        var prompt = $"Bad choices {Guid.NewGuid():N}?";

        // All four columns present, but the fourth cell is blank: the same "a choice cannot be
        // blank" rule Question.Validate already enforces for the single-question form.
        var row = ChoiceRow(prompt);
        row[Array.IndexOf(ChoiceHeader, "choice4")] = "";

        var report = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, row));

        var result = report.Rows.Single();
        Assert.False(result.Accepted);
        Assert.Equal("bad_choices", result.Error);
    }

    [Fact]
    public async Task A_sorting_row_is_accepted_in_its_stored_order_with_no_correct_index()
    {
        using var client = AdminClient();
        var prompt = $"Order these {Guid.NewGuid():N}";

        var report = await ImportAsync(client, "sort", "csv", Csv(SortHeader, SortRow(prompt)));

        Assert.Equal(1, report.Accepted);
        Assert.True(report.Rows.Single().Accepted);
    }

    // --- dry run vs. commit -----------------------------------------------------------

    [Fact]
    public async Task A_dry_run_never_changes_the_question_count()
    {
        using var client = AdminClient();
        var before = LiveShared.Questions.Items.Count;
        var prompt = $"Dry run only {Guid.NewGuid():N}?";

        var report = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, ChoiceRow(prompt)), dryRun: true);

        Assert.Equal(1, report.Accepted);
        Assert.Equal(before, LiveShared.Questions.Items.Count);
        Assert.DoesNotContain(LiveShared.Questions.Items, q => q.Prompt == prompt);
    }

    [Fact]
    public async Task Committing_inserts_exactly_the_accepted_rows_as_admin_authored_and_pending_by_default()
    {
        using var client = AdminClient();
        var prompt = $"Commit me {Guid.NewGuid():N}?";

        var report = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, ChoiceRow(prompt)), dryRun: false);

        Assert.Equal(1, report.Accepted);
        var stored = LiveShared.Questions.Items.Single(q => q.Prompt == prompt);
        Assert.Equal(QuestionSource.Admin, stored.Source);
        Assert.Equal(QuestionKind.Choice, stored.Kind);
        Assert.Equal(QuestionStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task An_explicit_status_column_is_honoured_on_commit()
    {
        using var client = AdminClient();
        var prompt = $"Approved on import {Guid.NewGuid():N}?";

        await ImportAsync(client, "choice", "csv",
            Csv(ChoiceHeader, ChoiceRow(prompt, status: "approved")), dryRun: false);

        var stored = LiveShared.Questions.Items.Single(q => q.Prompt == prompt);
        Assert.Equal(QuestionStatus.Approved, stored.Status);
    }

    [Fact]
    public async Task An_unrecognised_status_value_rejects_the_row_rather_than_silently_defaulting()
    {
        using var client = AdminClient();
        var prompt = $"Bad status {Guid.NewGuid():N}?";

        var report = await ImportAsync(client, "choice", "csv",
            Csv(ChoiceHeader, ChoiceRow(prompt, status: "definitely-not-a-status")));

        var result = report.Rows.Single();
        Assert.False(result.Accepted);
        Assert.Equal("bad_status", result.Error);
    }

    // --- TopicKey dedup -----------------------------------------------------------------

    [Fact]
    public async Task Two_rows_in_one_file_with_the_same_topic_report_one_accepted_and_one_rejected()
    {
        using var client = AdminClient();
        var subject = $"subject{Guid.NewGuid():N}";
        var aspect = "capital";
        var first = $"First phrasing {Guid.NewGuid():N}?";
        var second = $"Second phrasing {Guid.NewGuid():N}?";

        var body = Csv(ChoiceHeader, ChoiceRow(first, subject, aspect), ChoiceRow(second, subject, aspect));

        var report = await ImportAsync(client, "choice", "csv", body, dryRun: false);

        Assert.Equal(1, report.Accepted);
        Assert.Equal(1, report.Rejected);
        Assert.Single(report.Rows, r => r.Accepted);
        Assert.Single(report.Rows, r => !r.Accepted && r.Error == "duplicate_topic");
        Assert.Single(LiveShared.Questions.Items, q => q.Prompt == first || q.Prompt == second);
    }

    [Fact]
    public async Task An_invalid_row_does_not_reserve_its_topic_for_a_later_row()
    {
        using var client = AdminClient();
        var subject = $"subject-{Guid.NewGuid():N}";
        var aspect = "capital";

        var invalid = ChoiceRow($"Invalid {Guid.NewGuid():N}?", subject, aspect);
        invalid[Array.IndexOf(ChoiceHeader, "choice1")] = ""; // blank choice -> bad_choices, not accepted
        var valid = ChoiceRow($"Valid {Guid.NewGuid():N}?", subject, aspect);

        var report = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, invalid, valid));

        Assert.Equal(1, report.Accepted);
        Assert.Equal("bad_choices", report.Rows[0].Error);
        Assert.True(report.Rows[1].Accepted);
    }

    [Fact]
    public async Task A_topic_matching_an_already_stored_question_in_the_same_language_is_rejected()
    {
        using var client = AdminClient();
        var subject = $"subject-{Guid.NewGuid():N}";
        var aspect = "capital";

        await ImportAsync(client, "choice", "csv",
            Csv(ChoiceHeader, ChoiceRow($"Already there {Guid.NewGuid():N}?", subject, aspect)), dryRun: false);

        var report = await ImportAsync(client, "choice", "csv",
            Csv(ChoiceHeader, ChoiceRow($"Same topic, new wording {Guid.NewGuid():N}?", subject, aspect)));

        Assert.Equal("duplicate_topic", report.Rows.Single().Error);
    }

    [Fact]
    public async Task The_same_topic_in_a_different_language_is_unaffected()
    {
        using var client = AdminClient();
        var subject = $"subject-{Guid.NewGuid():N}";
        var aspect = "capital";

        var enRow = ChoiceRow($"English phrasing {Guid.NewGuid():N}?", subject, aspect);
        await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, enRow), dryRun: false);

        var nlRow = ChoiceRow($"Dutch phrasing {Guid.NewGuid():N}?", subject, aspect);
        nlRow[Array.IndexOf(ChoiceHeader, "lang")] = "nl";

        var report = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, nlRow));

        Assert.True(report.Rows.Single().Accepted);
    }

    [Theory]
    [InlineData("only-subject", "")]
    [InlineData("", "only-aspect")]
    [InlineData("", "")]
    public async Task A_row_missing_one_or_both_topic_halves_is_never_dedup_rejected(string subject, string aspect)
    {
        using var client = AdminClient();
        var subjectValue = subject.Length > 0 ? $"{subject}-{Guid.NewGuid():N}" : "";
        var aspectValue = aspect.Length > 0 ? $"{aspect}-{Guid.NewGuid():N}" : "";

        var body = Csv(ChoiceHeader,
            ChoiceRow($"First {Guid.NewGuid():N}?", subjectValue, aspectValue),
            ChoiceRow($"Second {Guid.NewGuid():N}?", subjectValue, aspectValue));

        var report = await ImportAsync(client, "choice", "csv", body);

        Assert.Equal(2, report.Accepted);
        Assert.All(report.Rows, r => Assert.True(r.Accepted));
    }

    // --- CSV and JSON agree -------------------------------------------------------------

    [Fact]
    public async Task An_equivalent_CSV_row_and_JSON_row_produce_the_same_outcome()
    {
        using var client = AdminClient();
        var prompt = $"Same either way {Guid.NewGuid():N}?";

        var csvReport = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, ChoiceRow(prompt)));

        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                lang = "en", categoryId = "geography", level = 2, prompt,
                choice1 = "Sargasso Sea", choice2 = "Baltic Sea", choice3 = "Red Sea", choice4 = "Black Sea",
                correctIndex = 0
            }
        });
        var jsonReport = await ImportAsync(client, "choice", "json", json);

        Assert.True(csvReport.Rows.Single().Accepted);
        Assert.Equal(csvReport.Rows.Single().Accepted, jsonReport.Rows.Single().Accepted);
        Assert.Equal(csvReport.Rows.Single().Error, jsonReport.Rows.Single().Error);
    }

    [Fact]
    public async Task An_equivalent_invalid_CSV_row_and_JSON_row_are_both_rejected_with_the_same_code()
    {
        using var client = AdminClient();
        var prompt = $"Both wrong {Guid.NewGuid():N}?";

        var badChoiceRow = ChoiceRow(prompt);
        badChoiceRow[Array.IndexOf(ChoiceHeader, "choice2")] = "";
        var csvReport = await ImportAsync(client, "choice", "csv", Csv(ChoiceHeader, badChoiceRow));

        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                lang = "en", categoryId = "geography", level = 2, prompt,
                choice1 = "Sargasso Sea", choice2 = "", choice3 = "Red Sea", choice4 = "Black Sea",
                correctIndex = 0
            }
        });
        var jsonReport = await ImportAsync(client, "choice", "json", json);

        Assert.Equal("bad_choices", csvReport.Rows.Single().Error);
        Assert.Equal(csvReport.Rows.Single().Error, jsonReport.Rows.Single().Error);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
