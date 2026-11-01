using System.Net.Http.Json;
using Quesshi.Features.Leaderboard;
using Quesshi.Features.Stats;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("Stats board total endpoints")]
public sealed class StatsBoardTotalEndpointTests : IAsyncLifetime
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!LeaderboardReadSupport.IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new StatsFeature(), new LeaderboardFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "stats", true);
        await _host.ExecuteOwnerAsync("""
            insert into player_stats(tenant_id, player_id, format, played, wins, points)
            values (@tenantId, @playerId, 'async_duel', 5, 3, 40), (@tenantId, @playerId, 'live', 2, 1, 25);
            """, new { tenantId = _host.TenantId, playerId = _host.PlayerId });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    /// <summary>AC-4.</summary>
    [SkippableFact]
    public async Task Total_is_the_all_time_board_score_when_leaderboard_score_is_offered()
    {
        await _host!.SetCapabilityAsync("feature", "leaderboard", true);
        await LeaderboardReadSupport.SetScoresAsync(_host, "all", "all", (Host.PlayerId, 17));
        await LeaderboardReadSupport.SetScoresAsync(_host, "all", "2026-10", (Host.PlayerId, 900));

        var summary = await GetSummaryAsync();

        Assert.Equal(17, summary.Total);
    }

    /// <summary>AC-4.</summary>
    [SkippableFact]
    public async Task Total_is_the_board_score_even_when_the_player_has_no_board_row()
    {
        await _host!.SetCapabilityAsync("feature", "leaderboard", true);

        var summary = await GetSummaryAsync();

        Assert.Equal(0, summary.Total);
    }

    /// <summary>AC-5.</summary>
    [SkippableFact]
    public async Task Total_is_the_points_earned_across_formats_when_leaderboard_score_is_unavailable()
    {
        await _host!.SetCapabilityAsync("feature", "leaderboard", false);
        await LeaderboardReadSupport.SetScoresAsync(_host, "all", "all", (Host.PlayerId, 17));

        var summary = await GetSummaryAsync();

        Assert.Equal(65, summary.Total);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!LeaderboardReadSupport.IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP fixture did not start.");
        }
    }

    private async Task<StatsPlayerSummary> GetSummaryAsync()
    {
        using var response = await Host.Player.GetAsync("/api/x/stats/me");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StatsPlayerSummary>())!;
    }
}
