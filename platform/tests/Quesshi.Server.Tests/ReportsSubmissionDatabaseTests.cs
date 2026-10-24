using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Features.Features.Reports;
using Quesshi.Features.Sources.Bank;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class ReportsSubmissionDatabaseTests : IAsyncLifetime
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
    public async Task Player_can_report_a_question_from_the_sixtieth_most_recent_archived_session()
    {
        var questionId = await CreateQuestionAsync();
        await SeedArchivedHistoryAsync(Host.PlayerId, questionId, 60, 1);

        using var response = await SubmitAsync(Host.Player, questionId, "WrongAnswer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Repeated_report_returns_already_reported_without_another_row()
    {
        var questionId = await CreateEligibleQuestionAsync(Host.PlayerId);
        using var first = await SubmitAsync(Host.Player, questionId, "Unclear");
        using var second = await SubmitAsync(Host.Player, questionId, "Unclear");
        var report = await second.Content.ReadFromJsonAsync<ReportResponse>();

        Assert.Equal((HttpStatusCode.OK, true, true, 1L), (second.StatusCode, report?.Reported, report?.AlreadyReported, await ReportCountAsync(questionId)));
    }

    [SkippableFact]
    public async Task Player_cannot_report_a_question_absent_from_archived_sessions()
    {
        var questionId = await CreateQuestionAsync();

        using var response = await SubmitAsync(Host.Player, questionId, "Duplicate");
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();

        Assert.Equal((HttpStatusCode.BadRequest, "not_your_question"), (response.StatusCode, error?.Error));
    }

    [SkippableFact]
    public async Task Player_cannot_report_a_question_only_in_the_sixty_first_most_recent_archived_session()
    {
        var questionId = await CreateQuestionAsync();
        await SeedArchivedHistoryAsync(Host.PlayerId, questionId, 61, 1);

        using var response = await SubmitAsync(Host.Player, questionId, "Offensive");
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();

        Assert.Equal((HttpStatusCode.BadRequest, "not_your_question"), (response.StatusCode, error?.Error));
    }

    [SkippableFact]
    public async Task Player_cannot_report_with_an_unknown_reason()
    {
        var questionId = await CreateQuestionAsync();

        using var response = await SubmitAsync(Host.Player, questionId, "NotAReportReason");
        var error = await response.Content.ReadFromJsonAsync<ErrorDocument>();

        Assert.Equal((HttpStatusCode.BadRequest, "bad_reason"), (response.StatusCode, error?.Error));
    }

    [SkippableFact]
    public async Task Guest_cannot_submit_a_report()
    {
        using var response = await SubmitAsync(Host.Guest, Guid.NewGuid(), "Other");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Third_distinct_report_moves_an_approved_question_to_pending()
    {
        var questionId = await CreateQuestionAsync();
        var reporters = await CreateEligibleReportersAsync(questionId, 3);

        foreach (var (client, _) in reporters)
        {
            using var response = await SubmitAsync(client, questionId, "Other");
            response.EnsureSuccessStatusCode();
        }

        Assert.Equal("pending", await QuestionStatusAsync(questionId));
    }

    [SkippableFact]
    public async Task Bank_source_does_not_draw_a_question_suppressed_by_three_reports()
    {
        var categoryId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into categories (tenant_id, id, slug, names, is_active)
            values (@tenantId, @categoryId, @slug, '{"en":"Reports"}'::jsonb, true);
            insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings)
            values (@tenantId, 'source', 'bank', true, true, @settings::jsonb)
            on conflict (tenant_id, type, key) do update
            set entitled = true, enabled = true, settings = excluded.settings;
            """, new
        {
            tenantId = Host.TenantId,
            categoryId,
            slug = $"reports-{categoryId:N}",
            settings = JsonSerializer.Serialize(new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [categoryId] })),
        });
        var questionId = await CreateQuestionAsync(categoryId);
        var reporters = await CreateEligibleReportersAsync(questionId, 3);
        foreach (var (client, _) in reporters)
        {
            using var response = await SubmitAsync(client, questionId, "WrongAnswer");
            response.EnsureSuccessStatusCode();
        }
        var database = Host.Services.GetRequiredService<ITenantDb>();

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => database.InTenantAsync(
            Host.TenantId,
            transaction => new BankSource().PickAsync(
                transaction,
                JsonSerializer.SerializeToElement(new BankParams()),
                new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 13),
                Host.PlayerId)));
    }

    [SkippableFact]
    public async Task Ten_simultaneous_valid_reports_complete_without_a_deadlock()
    {
        var questionId = await CreateQuestionAsync();
        var reporters = await CreateEligibleReportersAsync(questionId, 10);

        var responses = await Task.WhenAll(reporters.Select(pair => SubmitAsync(pair.Client, questionId, "Other")));
        using var disposal = new ResponseDisposal(responses);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
    }

    [SkippableFact]
    public async Task Ten_simultaneous_valid_reports_create_ten_rows()
    {
        var questionId = await CreateQuestionAsync();
        var reporters = await CreateEligibleReportersAsync(questionId, 10);
        var responses = await Task.WhenAll(reporters.Select(pair => SubmitAsync(pair.Client, questionId, "Other")));
        using var disposal = new ResponseDisposal(responses);

        Assert.Equal(10L, await ReportCountAsync(questionId));
    }

    [SkippableFact]
    public async Task Ten_simultaneous_valid_reports_move_the_question_to_pending()
    {
        var questionId = await CreateQuestionAsync();
        var reporters = await CreateEligibleReportersAsync(questionId, 10);
        var responses = await Task.WhenAll(reporters.Select(pair => SubmitAsync(pair.Client, questionId, "Other")));
        using var disposal = new ResponseDisposal(responses);

        Assert.Equal("pending", await QuestionStatusAsync(questionId));
    }

    [SkippableFact]
    public async Task Disabled_reports_capability_hides_the_submission_route()
    {
        await Host.SetCapabilityAsync("feature", "reports", false);

        using var response = await SubmitAsync(Host.Player, Guid.NewGuid(), "Other");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private async Task<Guid> CreateEligibleQuestionAsync(Guid playerId)
    {
        var questionId = await CreateQuestionAsync();
        await SeedArchivedHistoryAsync(playerId, questionId, 1, 1);
        return questionId;
    }

    private async Task<Guid> CreateQuestionAsync(Guid? categoryId = null)
    {
        var questionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
            values (@tenantId, @questionId, 'choice', 1, 'trivia', 'en', @categoryId, 1, @prompt,
                    '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, @prompt, 'approved', 'admin')
            """, new { tenantId = Host.TenantId, questionId, categoryId, prompt = $"report-{questionId:N}" });
        return questionId;
    }

    private async Task<IReadOnlyList<(HttpClient Client, Guid PlayerId)>> CreateEligibleReportersAsync(Guid questionId, int count)
    {
        var reporters = new List<(HttpClient Client, Guid PlayerId)> { (Host.Player, Host.PlayerId) };
        while (reporters.Count < count) reporters.Add(await Host.CreateSecondPlayerClientAsync());
        foreach (var (_, playerId) in reporters) await SeedArchivedHistoryAsync(playerId, questionId, 1, 1);
        return reporters;
    }

    private Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid questionId, string reason) =>
        client.PostAsJsonAsync("/api/x/reports", new { questionId, reason, note = "test" });

    private async Task SeedArchivedHistoryAsync(Guid playerId, Guid questionId, int count, int questionOrdinal)
    {
        var sessionIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await Host.ExecuteOwnerAsync("""
            with history as (
                select id, ordinal
                from unnest(@sessionIds::uuid[]) with ordinality as session_values(id, ordinal)
            ), created as (
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, created_at, started_at, ended_at)
                select @tenantId, id, concat('report-', substring(replace(id::text, '-', '') from 1 for 16)), 'reports-test',
                       @playerId, 'en', 'finished', 1, '{}'::jsonb,
                       @startAt + ordinal * interval '1 minute', @startAt + ordinal * interval '1 minute', @startAt + ordinal * interval '1 minute'
                from history
                returning id
            )
            insert into session_participants (tenant_id, session_id, player_id, seat)
            select @tenantId, id, @playerId, 0
            from created;
            insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, card, reveal)
            select @tenantId, id, 0, @questionId, 'choice', 1, 'Reported question', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb
            from unnest(@sessionIds::uuid[]) with ordinality as session_values(id, ordinal)
            where ordinal = @questionOrdinal;
            """, new
        {
            tenantId = Host.TenantId,
            sessionIds,
            playerId,
            questionId,
            questionOrdinal,
            startAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
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

    private sealed record ErrorDocument(string Error);

    private sealed record ReportResponse(bool Reported, bool AlreadyReported);

    private sealed class ResponseDisposal(IReadOnlyList<HttpResponseMessage> responses) : IDisposable
    {
        public void Dispose()
        {
            foreach (var response in responses) response.Dispose();
        }
    }
}
