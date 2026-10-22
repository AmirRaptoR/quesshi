using System.Net.Http.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class AdminDashboardEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new DashboardHttpFeature()]);
        await _host.StartAsync();
        await SeedDashboardAsync(_host);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Moderator_dashboard_returns_the_seeded_player_count()
    {
        var dashboard = await GetDashboardAsync();

        Assert.Equal(3, dashboard.Players);
    }

    [SkippableFact]
    public async Task Moderator_dashboard_returns_each_seeded_session_status_count()
    {
        var dashboard = await GetDashboardAsync();

        Assert.Equal(1, Count(dashboard.SessionStatuses, "lobby"));
        Assert.Equal(1, Count(dashboard.SessionStatuses, "running"));
        Assert.Equal(1, Count(dashboard.SessionStatuses, "finished"));
        Assert.Equal(1, Count(dashboard.SessionStatuses, "cancelled"));
    }

    [SkippableFact]
    public async Task Moderator_dashboard_returns_each_seeded_session_format_count()
    {
        var dashboard = await GetDashboardAsync();

        Assert.Equal(2, Count(dashboard.SessionFormats, "async_duel"));
        Assert.Equal(2, Count(dashboard.SessionFormats, "poll"));
    }

    [SkippableFact]
    public async Task Moderator_dashboard_returns_each_seeded_question_status_count()
    {
        var dashboard = await GetDashboardAsync();

        Assert.Equal(1, Count(dashboard.QuestionStatuses, "draft"));
        Assert.Equal(1, Count(dashboard.QuestionStatuses, "pending"));
        Assert.Equal(1, Count(dashboard.QuestionStatuses, "approved"));
        Assert.Equal(1, Count(dashboard.QuestionStatuses, "rejected"));
    }

    [SkippableFact]
    public async Task Moderator_dashboard_excludes_plausible_rows_from_another_tenant()
    {
        var dashboard = await GetDashboardAsync();

        Assert.Equal(3, dashboard.Players);
        Assert.Equal(1, Count(dashboard.SessionStatuses, "finished"));
        Assert.Equal(1, Count(dashboard.QuestionStatuses, "approved"));
        Assert.DoesNotContain(dashboard.SessionFormats, count => count.Key == "foreign_format");
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private async Task<DashboardResponse> GetDashboardAsync()
    {
        using var response = await Host.Moderator.GetAsync("/api/admin/dashboard");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardResponse>()
            ?? throw new InvalidOperationException("The dashboard response was empty.");
    }

    private static long Count(IReadOnlyList<DashboardCount> counts, string key) =>
        Assert.Single(counts, count => count.Key == key).Count;

    private static async Task SeedDashboardAsync(PlatformHttpFixture host)
    {
        var extraPlayer = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var otherPlayer = Guid.NewGuid();
        await host.ExecuteOwnerAsync("""
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @extraPlayer, null, 'Dashboard guest', 'dashboard-guest', 'en');

            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at)
            values
                (@tenantId, @lobby, 'DASHLOBBY', 'async_duel', @playerId, 'en', 'lobby', 2, '{}'::jsonb, null),
                (@tenantId, @running, 'DASHRUN', 'poll', @playerId, 'en', 'running', 2, '{}'::jsonb, now());
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at, ended_at, end_reason)
            values
                (@tenantId, @finished, 'DASHDONE', 'async_duel', @playerId, 'en', 'finished', 2, '{}'::jsonb, now(), now(), 'forfeit'),
                (@tenantId, @cancelled, 'DASHCANCEL', 'poll', @playerId, 'en', 'cancelled', 2, '{}'::jsonb, now(), now(), 'admin');

            insert into questions(tenant_id, id, kind, family, lang, prompt, payload, status, source)
            values
                (@tenantId, @draft, 'choice', 'trivia', 'en', 'Draft dashboard question', '{}'::jsonb, 'draft', 'admin'),
                (@tenantId, @pending, 'choice', 'trivia', 'en', 'Pending dashboard question', '{}'::jsonb, 'pending', 'admin'),
                (@tenantId, @approved, 'choice', 'trivia', 'en', 'Approved dashboard question', '{}'::jsonb, 'approved', 'admin'),
                (@tenantId, @rejected, 'choice', 'trivia', 'en', 'Rejected dashboard question', '{}'::jsonb, 'rejected', 'admin');

            insert into tenants(id, slug, name, default_language)
            values (@otherTenant, @otherSlug, 'Other dashboard tenant', 'en');
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@otherTenant, @otherPlayer, null, 'Other dashboard player', 'other-dashboard', 'en');
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at, ended_at, end_reason)
            values (@otherTenant, @otherSession, 'FOREIGN', 'foreign_format', @otherPlayer, 'en', 'finished', 2, '{}'::jsonb, now(), now(), 'forfeit');
            insert into questions(tenant_id, id, kind, family, lang, prompt, payload, status, source)
            values (@otherTenant, @otherQuestion, 'choice', 'trivia', 'en', 'Other dashboard question', '{}'::jsonb, 'approved', 'admin');
            """, new
        {
            tenantId = host.TenantId,
            playerId = host.PlayerId,
            extraPlayer,
            lobby = Guid.NewGuid(),
            running = Guid.NewGuid(),
            finished = Guid.NewGuid(),
            cancelled = Guid.NewGuid(),
            draft = Guid.NewGuid(),
            pending = Guid.NewGuid(),
            approved = Guid.NewGuid(),
            rejected = Guid.NewGuid(),
            otherTenant,
            otherSlug = $"other-dashboard-{otherTenant:N}",
            otherPlayer,
            otherSession = Guid.NewGuid(),
            otherQuestion = Guid.NewGuid(),
        });
    }

    private sealed record DashboardResponse(
        long Players,
        IReadOnlyList<DashboardCount> SessionStatuses,
        IReadOnlyList<DashboardCount> SessionFormats,
        IReadOnlyList<DashboardCount> QuestionStatuses);

    private sealed record DashboardCount(string Key, long Count);

    private sealed class DashboardHttpFeature : IFeature
    {
        public string Id => "dashboard_http";
        public FeatureSettings Settings => FeatureSettings.Create(new DashboardHttpSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record DashboardHttpSettings;
}
