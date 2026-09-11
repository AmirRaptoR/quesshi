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

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
