using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Features.Reports;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class ReportsModerationEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new ReportsFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Moderator_lists_reported_questions_with_counts()
    {
        var questionId = await CreateQuestionAsync();
        await SeedReportAsync(questionId, "WrongAnswer");
        await SeedReportAsync(questionId, "Unclear");

        var reports = await GetReportsAsync(Host.Moderator);

        Assert.Equal((questionId, 2L), Assert.Single(reports) is { } report
            ? (report.QuestionId, report.Count)
            : throw new InvalidOperationException("Expected one reported question."));
    }

    [SkippableFact]
    public async Task Moderator_lists_recorded_report_reasons()
    {
        var questionId = await CreateQuestionAsync();
        await SeedReportAsync(questionId, "Offensive");
        await SeedReportAsync(questionId, "Duplicate");

        var reports = await GetReportsAsync(Host.Moderator);

        Assert.Equal(["Offensive", "Duplicate"], Assert.Single(reports).Reasons);
    }

    [SkippableFact]
    public async Task Reported_questions_are_sorted_by_descending_report_count()
    {
        var mostReportedQuestionId = await CreateQuestionAsync();
        var lessReportedQuestionId = await CreateQuestionAsync();
        await CreateQuestionAsync();
        await SeedReportAsync(mostReportedQuestionId, "WrongAnswer");
        await SeedReportAsync(mostReportedQuestionId, "Unclear");
        await SeedReportAsync(lessReportedQuestionId, "Other");

        var reports = await GetReportsAsync(Host.Moderator);

        Assert.Equal([mostReportedQuestionId, lessReportedQuestionId], reports.Select(static report => report.QuestionId));
    }

    [SkippableFact]
    public async Task Moderator_dismissal_deletes_the_questions_reports()
    {
        var questionId = await CreateQuestionAsync();
        await SeedReportAsync(questionId, "WrongAnswer");

        using var response = await DismissReportsAsync(Host.Moderator, questionId);
        response.EnsureSuccessStatusCode();

        Assert.Equal(0L, await ReportCountAsync(questionId));
    }

    [SkippableFact]
    public async Task Moderator_dismissal_approves_the_question()
    {
        var questionId = await CreateQuestionAsync();
        await SeedReportAsync(questionId, "WrongAnswer");
        await SetQuestionStatusAsync(questionId, "pending");

        using var response = await DismissReportsAsync(Host.Moderator, questionId);
        response.EnsureSuccessStatusCode();

        Assert.Equal("approved", await QuestionStatusAsync(questionId));
    }

    [SkippableFact]
    public async Task Two_new_distinct_reports_after_dismissal_keep_the_question_approved()
    {
        var questionId = await CreateDismissedQuestionAsync();
        var reporters = await CreateEligibleReportersAsync(questionId, 2);
        foreach (var reporter in reporters)
        {
            using var response = await SubmitAsync(reporter, questionId);
            response.EnsureSuccessStatusCode();
        }

        Assert.Equal("approved", await QuestionStatusAsync(questionId));
    }

    [SkippableFact]
    public async Task Third_new_distinct_report_after_dismissal_moves_the_question_to_pending()
    {
        var questionId = await CreateDismissedQuestionAsync();
        var reporters = await CreateEligibleReportersAsync(questionId, 3);
        foreach (var reporter in reporters)
        {
            using var response = await SubmitAsync(reporter, questionId);
            response.EnsureSuccessStatusCode();
        }

        Assert.Equal("pending", await QuestionStatusAsync(questionId));
    }

    [SkippableFact]
    public async Task Reports_list_requires_a_moderator_role()
    {
        await ReportsModerationRoleMatrix()
            .AssertAsync(client => client.GetAsync("/api/admin/x/reports"));
    }

    [SkippableFact]
    public async Task Reports_dismissal_requires_a_moderator_role()
    {
        var questionId = await CreateQuestionAsync();

        await ReportsModerationRoleMatrix()
            .AssertAsync(client => DismissReportsAsync(client, questionId));
    }

    [SkippableFact]
    public async Task Disabled_reports_capability_hides_the_moderation_list()
    {
        await Host.SetCapabilityAsync("feature", "reports", false);

        using var response = await Host.Moderator.GetAsync("/api/admin/x/reports");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private RoleMatrix ReportsModerationRoleMatrix() => new(
    [
        new("anonymous", Host.Anonymous, HttpStatusCode.Unauthorized),
        new("guest", Host.Guest, HttpStatusCode.Unauthorized),
        new("player", Host.Player, HttpStatusCode.Unauthorized),
        new("tenant moderator", Host.Moderator, HttpStatusCode.OK),
        new("editor", Host.Editor, HttpStatusCode.OK),
        new("owner", Host.Owner, HttpStatusCode.OK),
        new("platform administrator", Host.PlatformAdministrator, HttpStatusCode.OK),
    ]);

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private async Task<IReadOnlyList<ReportedQuestionResponse>> GetReportsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/admin/x/reports");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ReportedQuestionResponse>>() ?? [];
    }

    private async Task<Guid> CreateQuestionAsync()
    {
        var questionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into questions (tenant_id, id, kind, kind_version, family, lang, level, prompt, payload, fingerprint, status, source)
            values (@tenantId, @questionId, 'choice', 1, 'trivia', 'en', 1, @prompt,
                    '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, @prompt, 'approved', 'admin')
            """, new { tenantId = Host.TenantId, questionId, prompt = $"report-moderation-{questionId:N}" });
        return questionId;
    }

    private async Task SeedReportAsync(Guid questionId, string reason)
    {
        var (_, playerId) = await Host.CreateSecondPlayerClientAsync();
        await Host.ExecuteOwnerAsync("""
            insert into question_reports (tenant_id, question_id, player_id, reason)
            values (@tenantId, @questionId, @playerId, @reason)
            """, new { tenantId = Host.TenantId, questionId, playerId, reason });
    }

    private async Task<Guid> CreateDismissedQuestionAsync()
    {
        var questionId = await CreateQuestionAsync();
        await SeedReportAsync(questionId, "WrongAnswer");
        await SeedReportAsync(questionId, "Unclear");
        await SeedReportAsync(questionId, "Other");
        await SetQuestionStatusAsync(questionId, "pending");
        using var response = await DismissReportsAsync(Host.Moderator, questionId);
        response.EnsureSuccessStatusCode();
        return questionId;
    }

    private async Task<IReadOnlyList<HttpClient>> CreateEligibleReportersAsync(Guid questionId, int count)
    {
        var reporters = new List<(HttpClient Client, Guid PlayerId)> { (Host.Player, Host.PlayerId) };
        while (reporters.Count < count) reporters.Add(await Host.CreateSecondPlayerClientAsync());
        foreach (var (_, playerId) in reporters) await SeedArchivedSessionAsync(playerId, questionId);
        return reporters.Select(static reporter => reporter.Client).ToArray();
    }

    private Task<HttpResponseMessage> DismissReportsAsync(HttpClient client, Guid questionId) =>
        client.PostAsync($"/api/admin/x/reports/{questionId}/dismiss", null);

    private Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid questionId) =>
        client.PostAsJsonAsync("/api/x/reports", new { questionId, reason = "Other", note = "test" });

    private Task SeedArchivedSessionAsync(Guid playerId, Guid questionId)
    {
        var sessionId = Guid.NewGuid();
        return Host.ExecuteOwnerAsync("""
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, created_at, started_at, ended_at)
            values (@tenantId, @sessionId, @code, 'reports-test', @playerId, 'en', 'finished', 1, '{}'::jsonb,
                    @completedAt, @completedAt, @completedAt);
            insert into session_participants (tenant_id, session_id, player_id, seat)
            values (@tenantId, @sessionId, @playerId, 0);
            insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, card, reveal)
            values (@tenantId, @sessionId, 0, @questionId, 'choice', 1, 'Reported question', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb);
            """, new
        {
            tenantId = Host.TenantId,
            sessionId,
            code = sessionId.ToString("N")[..16],
            playerId,
            questionId,
            completedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
    }

    private async Task<long> ReportCountAsync(Guid questionId) => Assert.Single(await Host.QueryOwnerAsync<long>("""
        select count(*)
        from question_reports
        where tenant_id = @tenantId and question_id = @questionId
        """, new { tenantId = Host.TenantId, questionId }));

    private async Task<string> QuestionStatusAsync(Guid questionId) => Assert.Single(await Host.QueryOwnerAsync<string>("""
        select status
        from questions
        where tenant_id = @tenantId and id = @questionId
        """, new { tenantId = Host.TenantId, questionId }));

    private Task SetQuestionStatusAsync(Guid questionId, string status) => Host.ExecuteOwnerAsync("""
        update questions
        set status = @status
        where tenant_id = @tenantId and id = @questionId
        """, new { tenantId = Host.TenantId, questionId, status });

    private sealed record ReportedQuestionResponse(Guid QuestionId, long Count, IReadOnlyList<string> Reasons);
}
