using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public sealed class MatchingQuestionImportEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"matching-import-{Guid.NewGuid():N}", "admin", "admin@example.com",
            "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private static string CategoryId => $"import-{Guid.NewGuid():N}";

    private static async Task SeedCategoryAsync(HttpClient client, string id, bool active = true)
    {
        var response = await client.PostAsJsonAsync("/api/admin/matching/categories",
            new MatchingCategoryDto(id, "واردات", "Import", "Import", "◆", "#123456", active, 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Csv(IEnumerable<string> header, params IEnumerable<string>[] rows)
    {
        var builder = new StringBuilder(string.Join(',', header)).Append('\n');
        foreach (var row in rows) builder.Append(string.Join(',', row.Select(CsvField))).Append('\n');
        return builder.ToString();
    }

    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string[] Header => ["lang", "categoryId", "prompt", "answerSource", "choice1", "choice2",
        "choice3", "choice4", "choice5", "choice6", "choice7", "choice8", "mediaUrl", "mediaKind",
        "subject", "aspect", "status"];

    private static string[] Row(string category, string prompt, string source = "participants",
        string subject = "", string aspect = "", string status = "")
        => ["en", category, prompt, source, "", "", "", "", "", "", "", "", "", "", subject, aspect, status];

    private static string[] FixedRow(string category, string prompt, params string[] choices)
    {
        var row = Row(category, prompt, "fixed");
        for (var i = 0; i < choices.Length && i < 8; i++) row[4 + i] = choices[i];
        return row;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string format, string body,
        bool dryRun = true)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(body, Encoding.UTF8), "file", $"matching.{format}" }
        };
        return await client.PostAsync($"/api/admin/matching/questions/import?format={format}&dryRun={dryRun}", form);
    }

    private static async Task<ImportReportDto> ImportAsync(HttpClient client, string format, string body,
        bool dryRun = true)
    {
        var response = await PostAsync(client, format, body, dryRun);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImportReportDto>())!;
    }

    [Fact]
    public async Task Dry_run_is_default_and_accepts_participant_rows_without_writing()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var prompt = $"Who leaves the lights on {Guid.NewGuid():N}?";

        var report = await ImportAsync(client, "csv", Csv(Header, Row(category, prompt)));

        Assert.True(report.DryRun);
        Assert.Equal(1, report.Accepted);
        Assert.DoesNotContain((await client.GetFromJsonAsync<AdminMatchingQuestionPageDto>(
            "/api/admin/matching/questions?text=lights"))!.Items, q => q.Prompt == prompt);
    }

    [Fact]
    public async Task Fixed_choices_are_trimmed_and_blank_choice_columns_are_absent()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var prompt = $"Fixed {Guid.NewGuid():N}";
        var row = FixedRow(category, prompt, " First ", "Second", "", "");

        var report = await ImportAsync(client, "csv", Csv(Header, row), dryRun: false);

        Assert.True(report.Rows.Single().Accepted);
        var saved = (await client.GetFromJsonAsync<AdminMatchingQuestionPageDto>(
            $"/api/admin/matching/questions?text={Uri.EscapeDataString(prompt)}"))!.Items.Single();
        Assert.Equal(["First", "Second"], saved.Choices);
    }

    [Fact]
    public async Task Per_row_answer_source_and_category_errors_do_not_abort_following_rows()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var badParticipants = Row(category, "bad source", "participants");
        badParticipants[4] = "not allowed";
        var badCategory = Row("geography", "trivia category");
        var good = Row(category, "good row");

        var report = await ImportAsync(client, "csv", Csv(Header, badParticipants, badCategory, good));

        Assert.Equal(3, report.Total);
        Assert.Equal(["choices_not_allowed", "unknown_category", null], report.Rows.Select(r => r.Error));
        Assert.Equal(1, report.Accepted);
    }

    [Fact]
    public async Task Topic_deduplication_is_per_language_and_applies_within_the_file()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var subject = $"subject-{Guid.NewGuid():N}";
        var first = Row(category, "first", subject: subject, aspect: "aspect");
        var second = Row(category, "second", subject: subject, aspect: "aspect");
        var dutch = Row(category, "dutch", subject: subject, aspect: "aspect");
        dutch[0] = "nl";

        var report = await ImportAsync(client, "csv", Csv(Header, first, second, dutch));

        Assert.Equal(2, report.Accepted);
        Assert.Equal("duplicate_topic", report.Rows[1].Error);
    }

    [Fact]
    public async Task CSV_accepts_BOM_case_insensitive_headers_and_quoted_newlines()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var body = Csv(Header.Select((h, i) => i == 0 ? "\uFEFF" + h.ToUpperInvariant() : h.ToUpperInvariant()),
            Row(category, "A, quoted\nquestion"));

        var report = await ImportAsync(client, "csv", body);

        Assert.True(report.Rows.Single().Accepted);
        Assert.Equal("A, quoted\nquestion", report.Rows.Single().Prompt);
    }

    [Fact]
    public async Task Templates_use_the_seeded_matching_category_and_both_formats_are_importable()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client, "partners");

        foreach (var format in new[] { "csv", "json" })
        {
            var response = await client.GetAsync($"/api/admin/matching/questions/import/template?format={format}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("matching-template", response.Content.Headers.ContentDisposition?.FileName);
            var body = await response.Content.ReadAsStringAsync();
            var report = await ImportAsync(client, format, body);
            Assert.Equal(1, report.Accepted);
        }
    }

    [Fact]
    public async Task Whole_document_errors_are_request_errors_and_missing_columns_are_not_rows()
    {
        using var client = AdminClient();
        var badJson = await PostAsync(client, "json", "{not json]");
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Equal("bad_json", (await badJson.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var missingHeader = await PostAsync(client, "csv", "lang,prompt\nen,hello\n");
        Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);
        Assert.Equal("bad_row", (await missingHeader.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
