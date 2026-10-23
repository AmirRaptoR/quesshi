using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Features.Stats;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("Stats read endpoints")]
public sealed class StatsReadEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;
    private Guid _categoryId;
    private Guid _secondPlayerId;
    private Guid _servedQuestionId;
    private Guid _unaskedQuestionId;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new StatsFeature(), new StatsQueryConsumerFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "stats", true);
        (_, _secondPlayerId) = await _host.CreateSecondPlayerClientAsync();
        _categoryId = Guid.NewGuid();
        _servedQuestionId = Guid.NewGuid();
        _unaskedQuestionId = Guid.NewGuid();
        await SeedAsync(_host);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Player_gets_own_per_format_summary()
    {
        var summary = await GetSummaryAsync(Host.Player, "/api/x/stats/me");

        Assert.Equivalent(new StatsPlayerSummary(Host.PlayerId,
            [new StatsFormatSummary("async_duel", 12, 8, 3, 1, 73, 2, 5)],
            [new StatsCategorySummary(_categoryId, 6, 4.5, new Dictionary<string, string> { ["en"] = "Current history", ["fa"] = "تاریخ" })]), summary);
    }

    [SkippableFact]
    public async Task Player_gets_another_same_tenant_players_summary()
    {
        var summary = await GetSummaryAsync(Host.Player, $"/api/x/stats/players/{_secondPlayerId}");

        Assert.Equivalent(new StatsPlayerSummary(_secondPlayerId,
            [new StatsFormatSummary("async_duel", 4, 1, 2, 1, 11, 0, 3)], []), summary);
    }

    [SkippableFact]
    public async Task Summary_uses_current_tenant_category_names()
    {
        var summary = await GetSummaryAsync(Host.Player, "/api/x/stats/me");

        Assert.Equivalent(new Dictionary<string, string> { ["en"] = "Current history", ["fa"] = "تاریخ" }, Assert.Single(summary.Categories).Names);
    }

    [SkippableFact]
    public async Task Anonymous_cannot_read_own_stats()
    {
        using var response = await Host.Anonymous.GetAsync("/api/x/stats/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Anonymous_cannot_read_another_players_stats()
    {
        using var response = await Host.Anonymous.GetAsync($"/api/x/stats/players/{_secondPlayerId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Player_cannot_read_a_plausible_foreign_tenant_summary()
    {
        var (_, foreignPlayerId) = await Host.CreateOtherTenantPlayerWithIdAsync();
        await Host.ExecuteOwnerAsync("""
            insert into player_stats(tenant_id, player_id, format, played, wins, points, streak, best_streak)
            select tenant_id, id, 'async_duel', 9, 9, 99, 9, 9 from players where id = @foreignPlayerId;
            """, new { foreignPlayerId });

        using var response = await Host.Player.GetAsync($"/api/x/stats/players/{foreignPlayerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Named_summary_query_is_available_when_stats_is_enabled()
    {
        var result = await InvokeSummaryAsync(Host.PlayerId);

        Assert.True(result.Available);
    }

    [SkippableFact]
    public async Task Named_summary_query_matches_the_player_summary_route()
    {
        var route = await GetSummaryAsync(Host.Player, "/api/x/stats/me");
        var query = await InvokeSummaryAsync(Host.PlayerId);

        Assert.Equivalent(route, query.Value);
    }

    [SkippableFact]
    public async Task Editor_gets_question_usage_with_accuracy_from_stored_credit()
    {
        var usage = await GetUsageAsync(Host.Editor);

        Assert.Equivalent(new[]
        {
            new StatsQuestionUsage(_servedQuestionId, 12, 4, .75),
            new StatsQuestionUsage(_unaskedQuestionId, 7, 0, 0),
        }, usage);
    }

    [SkippableFact]
    public async Task Owner_gets_question_usage()
    {
        var usage = await GetUsageAsync(Host.Owner);

        Assert.Equivalent(new[]
        {
            new StatsQuestionUsage(_servedQuestionId, 12, 4, .75),
            new StatsQuestionUsage(_unaskedQuestionId, 7, 0, 0),
        }, usage);
    }

    [SkippableFact]
    public async Task Player_cannot_read_admin_question_usage()
    {
        using var response = await Host.Player.GetAsync(UsagePath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Player_stats_route_is_unavailable_when_stats_is_disabled()
    {
        await Host.SetCapabilityAsync("feature", "stats", false);
        using var response = await Host.Player.GetAsync("/api/x/stats/me");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_stats_route_is_unavailable_when_stats_is_disabled()
    {
        await Host.SetCapabilityAsync("feature", "stats", false);
        using var response = await Host.Owner.GetAsync(UsagePath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP fixture did not start.");
        }
    }

    private string UsagePath => $"/api/admin/x/stats/questions?ids={_servedQuestionId},{_unaskedQuestionId}";

    private async Task<NamedQueryResult<StatsPlayerSummary?>> InvokeSummaryAsync(Guid playerId)
    {
        var database = Host.Services.GetRequiredService<ITenantDb>();
        var queries = Host.Services.GetRequiredService<INamedQueries>();
        return await database.InTenantAsync(Host.TenantId, transaction =>
            queries.InvokeAsync<StatsPlayerSummary?>("stats_read_test", "stats.summary", transaction, playerId, CancellationToken.None), readOnly: true);
    }

    private async Task<IReadOnlyList<StatsQuestionUsage>> GetUsageAsync(HttpClient client)
    {
        using var response = await client.GetAsync(UsagePath);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<StatsQuestionUsage>>() ?? [];
    }

    private static async Task<StatsPlayerSummary> GetSummaryAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StatsPlayerSummary>())!;
    }

    private async Task SeedAsync(PlatformHttpFixture host)
    {
        await host.ExecuteOwnerAsync("""
            insert into categories(tenant_id, id, slug, names)
            values (@tenantId, @categoryId, 'history', '{"en":"Current history","fa":"تاریخ"}'::jsonb);
            insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
            values (@tenantId, @servedQuestionId, 'choice', 1, 'trivia', 'en', @categoryId, 1, 'Served question',
                    '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, 'stats-served', 'approved', 'admin'),
                   (@tenantId, @unaskedQuestionId, 'choice', 1, 'trivia', 'en', @categoryId, 1, 'Unasked question',
                    '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, 'stats-unasked', 'approved', 'admin');
            insert into player_stats(tenant_id, player_id, format, played, wins, losses, draws, points, streak, best_streak)
            values (@tenantId, @playerId, 'async_duel', 12, 8, 3, 1, 73, 2, 5),
                   (@tenantId, @secondPlayerId, 'async_duel', 4, 1, 2, 1, 11, 0, 3);
            insert into player_category_stats(tenant_id, player_id, category_id, asked, credit_sum)
            values (@tenantId, @playerId, @categoryId, 6, 4.5);
            insert into question_stats(tenant_id, question_id, served, asked, credit_sum)
            values (@tenantId, @servedQuestionId, 12, 4, 3),
                   (@tenantId, @unaskedQuestionId, 7, 0, 0);
            """, new
            {
                tenantId = host.TenantId,
                playerId = host.PlayerId,
                secondPlayerId = _secondPlayerId,
                categoryId = _categoryId,
                servedQuestionId = _servedQuestionId,
                unaskedQuestionId = _unaskedQuestionId
            });
    }

    private sealed class StatsQueryConsumerFeature : IFeature
    {
        public string Id => "stats_read_test";

        public FeatureSettings Settings => FeatureSettings.Create(new ConsumerSettings(), static _ => []);

        public void Configure(IFeatureBuilder feature) => feature.Uses("stats.summary");

        private sealed record ConsumerSettings;
    }
}

[CollectionDefinition("Stats read endpoints", DisableParallelization = true)]
public sealed class StatsReadEndpointCollection;
