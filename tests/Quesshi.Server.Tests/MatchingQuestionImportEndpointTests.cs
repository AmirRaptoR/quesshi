using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Api;
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

    private static async Task<string> RequestErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

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
    public async Task Fixed_rows_require_two_choices_and_unknown_answer_sources_are_row_errors()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var tooFew = FixedRow(category, "too few", "only one");
        var unknown = Row(category, "unknown source", "sometimes");
        var good = FixedRow(category, "good", "one", "two");

        var report = await ImportAsync(client, "csv", Csv(Header, tooFew, unknown, good));

        Assert.Equal(["too_few_choices", "bad_answer_source", null], report.Rows.Select(r => r.Error));
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

    [Theory]
    [InlineData("subject-only", "")]
    [InlineData("", "aspect-only")]
    [InlineData("", "")]
    public async Task Missing_either_topic_half_disables_deduplication(string subject, string aspect)
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var unique = Guid.NewGuid().ToString("N");

        var report = await ImportAsync(client, "csv", Csv(Header,
            Row(category, $"first-{unique}", subject: subject, aspect: aspect),
            Row(category, $"second-{unique}", subject: subject, aspect: aspect)), dryRun: false);

        Assert.Equal(2, report.Accepted);
        Assert.All(report.Rows, row => Assert.Null(row.Error));
    }

    [Fact]
    public async Task Dry_run_and_commit_have_the_same_row_outcomes_for_a_topicless_file()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var body = Csv(Header, Row(category, $"same-report-{Guid.NewGuid():N}"));

        var dry = await ImportAsync(client, "csv", body);
        var committed = await ImportAsync(client, "csv", body, dryRun: false);

        Assert.True(dry.DryRun);
        Assert.False(committed.DryRun);
        Assert.Equal(dry.Total, committed.Total);
        Assert.Equal(dry.Accepted, committed.Accepted);
        Assert.Equal(dry.Rejected, committed.Rejected);
        Assert.Equal(dry.Rows, committed.Rows);
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
    public async Task CSV_rejects_unterminated_and_misplaced_quotes_as_row_errors()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);

        var misplaced = Row(category, "bad\"quote");
        var good = Row(category, "after misplaced quote");
        var unterminated = Row(category, "\"unterminated");
        // Keep the final row raw: CsvField would escape the opening quote and turn it into a valid
        // quoted value, which is precisely what this case must not do.
        var body = Csv(Header)
            + string.Join(',', misplaced) + "\n"
            + string.Join(',', good) + "\n"
            + string.Join(',', unterminated) + "\n";

        var report = await ImportAsync(client, "csv", body);

        Assert.Equal(3, report.Total);
        Assert.Equal(["bad_row", null, "bad_row"], report.Rows.Select(row => row.Error));
        Assert.Equal("after misplaced quote", report.Rows[1].Prompt);
        Assert.Equal(1, report.Accepted);
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

    [Fact]
    public async Task Malformed_row_numbers_are_data_row_ordinals()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var shortRow = string.Join(',', Row(category, "broken").Take(Header.Length - 1));
        var good = Row(category, "after broken");

        var report = await ImportAsync(client, "csv", Csv(Header) + shortRow + "\n" +
            string.Join(',', good.Select(CsvField)) + "\n" +
            string.Join(',', Row(category, "third").Select(CsvField)) + "\n");

        Assert.Equal([1, 2, 3], report.Rows.Select(row => row.Row));
        Assert.Equal("bad_row", report.Rows[0].Error);
        Assert.True(report.Rows[1].Accepted);
        Assert.True(report.Rows[2].Accepted);
    }

    [Theory]
    [InlineData("xml", "bad_format")]
    [InlineData("", "bad_format")]
    public async Task Unsupported_formats_are_refused_before_reading_rows(string format, string expected)
    {
        using var client = AdminClient();

        var response = await PostAsync(client, format, Csv(Header, Row("missing", "never read")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await RequestErrorAsync(response));
    }

    [Fact]
    public async Task An_empty_upload_is_a_request_error()
    {
        using var client = AdminClient();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([]), "file", "matching.csv" }
        };

        var response = await client.PostAsync("/api/admin/matching/questions/import?format=csv", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("empty_file", await RequestErrorAsync(response));
    }

    [Fact]
    public async Task An_upload_over_the_shared_twenty_megabyte_limit_is_refused_without_writing()
    {
        using var client = AdminClient();
        var body = new string('x', checked((int)QuestionImport.MaxImportBytes + 1));

        var response = await PostAsync(client, "csv", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("file_too_large", await RequestErrorAsync(response));
    }

    [Fact]
    public async Task An_upload_over_the_shared_row_limit_is_refused_without_a_partial_report()
    {
        using var client = AdminClient();
        var rows = Enumerable.Range(0, QuestionImport.MaxImportRows + 1)
            .Select(i => Row("missing", $"row-{i}"));

        var response = await PostAsync(client, "csv", Csv(Header, rows.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("too_many_rows", await RequestErrorAsync(response));
    }

    [Fact]
    public async Task Unknown_extra_columns_are_ignored_but_a_short_data_row_is_reported_and_following_rows_continue()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var header = Header.Append("futureColumn").ToArray();
        var shortRow = Row(category, "short row");
        var good = Row(category, $"good row {Guid.NewGuid():N}");
        var body = Csv(header, shortRow, good.Append("ignored"));

        var report = await ImportAsync(client, "csv", body);

        Assert.Equal(2, report.Total);
        Assert.Equal("bad_row", report.Rows[0].Error);
        Assert.Null(report.Rows[0].Prompt);
        Assert.True(report.Rows[1].Accepted);
    }

    [Fact]
    public async Task Inactive_and_trivia_categories_are_rejected_per_row_while_matching_rows_continue()
    {
        using var client = AdminClient();
        var inactive = CategoryId;
        var active = CategoryId;
        await SeedCategoryAsync(client, inactive, active: false);
        await SeedCategoryAsync(client, active);

        var report = await ImportAsync(client, "csv", Csv(Header,
            Row(inactive, "inactive"), Row("geography", "trivia"), Row(active, "valid")));

        Assert.Equal(["inactive_category", "unknown_category", null], report.Rows.Select(r => r.Error));
        Assert.Equal(1, report.Accepted);
    }

    [Fact]
    public async Task Commit_persists_status_media_source_and_topic_with_normalized_values()
    {
        using var client = AdminClient();
        var category = CategoryId;
        await SeedCategoryAsync(client, category);
        var row = FixedRow(category, "persisted");
        row[4] = " one ";
        row[5] = "two";
        row[12] = " https://example.test/picture.png ";
        row[13] = " IMAGE ";
        row[14] = "Subject";
        row[15] = "Aspect";
        row[16] = "APPROVED";

        var report = await ImportAsync(client, "csv", Csv(Header, row), dryRun: false);

        Assert.True(report.Rows.Single().Accepted);
        var saved = (await client.GetFromJsonAsync<AdminMatchingQuestionPageDto>(
            "/api/admin/matching/questions?text=persisted"))!.Items.Single();
        Assert.Equal("fixed", saved.AnswerSource);
        Assert.Equal(["one", "two"], saved.Choices);
        Assert.Equal("approved", saved.Status);
        Assert.Equal("admin", saved.Source);
        Assert.Equal("image", saved.Media!.Kind);
        Assert.Equal("https://example.test/picture.png", saved.Media.Url);
        Assert.Equal("subject|aspect", saved.Topic);
    }

    [Fact]
    public async Task The_matching_import_requires_admin_authorization_but_does_not_require_an_antiforgery_token()
    {
        using var anonymous = _host.NewClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Csv(Header, Row("missing", "unauthorized")), Encoding.UTF8), "file", "matching.csv" }
        };

        var response = await anonymous.PostAsync("/api/admin/matching/questions/import?format=csv", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_unique_index_race_marks_the_specific_losing_row_in_the_report()
    {
        var categories = new FakeMatchingCategories();
        categories.Items.Add(new MatchingCategory("m-race", "واردات", "Import", "◆", "#123456", true, 1));
        var questions = new RaceMatchingQuestions(rejectFirstWrite: true);
        var body = Csv(Header,
            Row("race", "first", subject: "first", aspect: "topic"),
            Row("race", "second", subject: "second", aspect: "topic"));

        var (error, report) = await MatchingQuestionImport.RunAsync("csv",
            new MemoryStream(Encoding.UTF8.GetBytes(body)), Encoding.UTF8.GetByteCount(body), false,
            questions, categories, new TimeProviderClock(TimeProvider.System), new FakeIdFactory());

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Equal([false, true], report!.Rows.Select(row => row.Accepted));
        Assert.Equal("duplicate_topic", report.Rows[0].Error);
        Assert.Equal(1, report.Accepted);
        Assert.Single(questions.Items, question => question.Prompt == "second");
    }

    private sealed class RaceMatchingQuestions(bool rejectFirstWrite) : IMatchingQuestionRepository
    {
        public readonly List<MatchingQuestion> Items = [];
        private bool _rejectFirstWrite = rejectFirstWrite;

        public Task<MatchingQuestion?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(question => question.Id == id));

        public Task<IReadOnlyList<MatchingQuestion>> FindAsync(MatchingQuestionFilter filter,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MatchingQuestion>>([]);

        public Task<long> CountAsync(MatchingQuestionFilter filter, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<MatchingQuestion>> SampleApprovedAsync(Language lang, string categoryId,
            int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MatchingQuestion>>([]);

        public Task UpsertAsync(MatchingQuestion question, CancellationToken ct = default)
        {
            if (_rejectFirstWrite)
            {
                _rejectFirstWrite = false;
                throw new InvalidOperationException("duplicate topic race");
            }

            Items.Add(question);
            return Task.CompletedTask;
        }

        public Task<MatchingServeResult> RecordServedAsync(string id, string serveToken,
            CancellationToken ct = default) => Task.FromResult(MatchingServeResult.Missing);

        public Task<int> UpsertManyAsync(IReadOnlyList<MatchingQuestion> questions,
            CancellationToken ct = default) => Task.FromResult(0);

        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(Items.Where(question => question.Lang == lang && question.Topic is not null)
                .Select(question => question.Topic!).ToHashSet());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
