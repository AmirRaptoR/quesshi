using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Features.Leaderboard;
using Quesshi.Plugin.Api;
using Quesshi.Shared;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

internal static class LeaderboardReadSupport
{
    public static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));

    public static async Task SetScoresAsync(PlatformHttpFixture host, string board, string period, params (Guid Player, long Score)[] scores)
    {
        foreach (var (player, score) in scores)
        {
            await host.ExecuteOwnerAsync("""
                insert into leaderboard_scores(tenant_id, board, period, player_id, score)
                values (@tenantId, @board, @period, @player, @score)
                on conflict (tenant_id, board, period, player_id) do update set score = excluded.score
                """, new { tenantId = host.TenantId, board, period, player, score });
        }
    }

    public static async Task<LeaderboardDto> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LeaderboardDto>())!;
    }
}

[Collection("Leaderboard read endpoints")]
public sealed class LeaderboardReadEndpointTests : IAsyncLifetime
{
    private PlatformHttpFixture? _host;
    private Guid _secondId;
    private Guid _thirdId;
    private Guid _outsiderId;
    private Guid _topId;

    public async Task InitializeAsync()
    {
        if (!LeaderboardReadSupport.IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new LeaderboardFeature(), new FriendsIdsProvider()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "leaderboard", true);
        await _host.SetCapabilityAsync("feature", "friends_ids_test", true);
        (_, _secondId) = await _host.CreateSecondPlayerClientAsync();
        (_, _thirdId) = await _host.CreateSecondPlayerClientAsync();
        (_, _outsiderId) = await _host.CreateSecondPlayerClientAsync();

        // 105 fillers with distinct scores 1010..2050, the first being the leader; the caller sits far below.
        await _host.ExecuteOwnerAsync("""
            insert into players(tenant_id, id, display_name, avatar_seed, lang)
            select @tenantId, gen_random_uuid(), 'Filler ' || n, 'seed-' || n, 'en' from generate_series(1, 105) n;
            insert into leaderboard_scores(tenant_id, board, period, player_id, score)
            select @tenantId, 'all', 'all', p.id, 1000 + 10 * substr(p.display_name, 8)::int
            from players p where p.display_name like 'Filler %';
            """, new { tenantId = _host.TenantId });
        _topId = await _host.Services.GetRequiredService<ITenantDb>().InTenantAsync(_host.TenantId, transaction =>
            transaction.QuerySingleOrDefaultAsync<Guid>("select id from players where display_name = 'Filler 105'"), readOnly: true);
        await LeaderboardReadSupport.SetScoresAsync(_host, "all", "all",
            (_host.PlayerId, 5), (_secondId, 1100), (_thirdId, 1100), (_outsiderId, 1200));
        await LeaderboardReadSupport.SetScoresAsync(_host, "async_duel", "2026-10",
            (_host.PlayerId, 50), (_secondId, 50), (_thirdId, 30));
        FriendsIdsProvider.Friends = [_secondId, _thirdId];
    }

    public async Task DisposeAsync()
    {
        FriendsIdsProvider.Friends = [];
        if (_host is not null) await _host.DisposeAsync();
    }

    /// <summary>AC-1.</summary>
    [SkippableFact]
    public async Task The_top_100_query_plan_uses_the_leaderboard_top_index()
    {
        var database = Host.Services.GetRequiredService<ITenantDb>();
        // Enough rows that the planner prefers the index for a limited, ordered read.
        await Host.ExecuteOwnerAsync("""
            with new_players as (
                insert into players(tenant_id, id, display_name, avatar_seed, lang)
                select @tenantId, gen_random_uuid(), 'Plan ' || n, 'plan-' || n, 'en' from generate_series(1, 5000) n
                returning id, display_name
            )
            insert into leaderboard_scores(tenant_id, board, period, player_id, score)
            select @tenantId, 'all', '2026-10', id, 1 + substr(display_name, 6)::int from new_players;
            analyze leaderboard_scores;
            """, new { tenantId = Host.TenantId });
        const string scans = "select coalesce(sum(idx_scan), 0)::bigint from pg_stat_user_indexes where indexrelname = 'leaderboard_top'";
        var before = await database.InTenantAsync(Host.TenantId, t => t.QuerySingleOrDefaultAsync<long>(scans), readOnly: true);

        await LeaderboardReadSupport.GetAsync(Host.Player, "/api/x/leaderboard/all?period=2026-10");

        // Index usage statistics reach other sessions asynchronously, so poll briefly.
        long after = before;
        for (var attempt = 0; attempt < 50 && after <= before; attempt++)
        {
            await Task.Delay(200);
            after = await database.InTenantAsync(Host.TenantId, t => t.QuerySingleOrDefaultAsync<long>(scans), readOnly: true);
        }

        Assert.True(after > before, "the top-100 read never scanned the leaderboard_top index");
    }

    /// <summary>AC-2.</summary>
    [SkippableFact]
    public async Task The_route_returns_at_most_100_entries_best_first_with_name_avatar_and_score()
    {
        var board = await LeaderboardReadSupport.GetAsync(Host.Player, "/api/x/leaderboard/all");

        Assert.Equal(100, board.Entries.Count);
        Assert.Equal(board.Entries.Select(entry => entry.Score).OrderByDescending(score => score), board.Entries.Select(entry => entry.Score));
        var leader = board.Entries[0];
        Assert.Equal((1, _topId, "Filler 105", "seed-105", 2050L), (leader.Rank, leader.PlayerId, leader.Name, leader.Avatar, leader.Score));
    }

    /// <summary>AC-3.</summary>
    [SkippableFact]
    public async Task Tied_players_share_a_rank_and_the_next_rank_skips_them()
    {
        var board = await LeaderboardReadSupport.GetAsync(Host.Player, "/api/x/leaderboard/async_duel?period=2026-10");

        Assert.Equal([(1, _host!.PlayerId), (1, _secondId), (3, _thirdId)],
            board.Entries.Select(entry => (entry.Rank, entry.PlayerId)).OrderBy(entry => entry.Rank).ThenBy(entry => entry.PlayerId == _host.PlayerId ? 0 : 1));
        Assert.Equal(1, board.Me!.Rank);
    }

    /// <summary>AC-4.</summary>
    [SkippableFact]
    public async Task The_caller_outside_the_top_100_still_gets_own_row_and_rank()
    {
        var board = await LeaderboardReadSupport.GetAsync(Host.Player, "/api/x/leaderboard/all");

        Assert.DoesNotContain(board.Entries, entry => entry.PlayerId == Host.PlayerId);
        // 105 fillers + 3 players at 1100/1200 all outrank a score of 5.
        Assert.Equal((109, Host.PlayerId, 5L), (board.Me!.Rank, board.Me.PlayerId, board.Me.Score));
    }

    /// <summary>AC-5.</summary>
    [SkippableFact]
    public async Task Friends_scope_ranks_only_the_caller_and_listed_friends_among_themselves()
    {
        var board = await LeaderboardReadSupport.GetAsync(Host.Player, "/api/x/leaderboard/all?scope=friends");

        Assert.Equal([(1, 1100L), (1, 1100L), (3, 5L)], board.Entries.Select(entry => (entry.Rank, entry.Score)));
        Assert.Equal(new[] { _secondId, _thirdId }.Order(), board.Entries.Take(2).Select(entry => entry.PlayerId).Order());
        Assert.DoesNotContain(board.Entries, entry => entry.PlayerId == _outsiderId);
        Assert.Equal((3, Host.PlayerId), (board.Me!.Rank, board.Me.PlayerId));
    }

    /// <summary>AC-7.</summary>
    [SkippableFact]
    public async Task The_score_query_returns_the_all_time_score_on_board_all_and_zero_without_a_row()
    {
        Assert.Equal((5L, 1100L, 0L), (
            await ScoreAsync(Host.PlayerId),
            await ScoreAsync(_secondId),
            await ScoreAsync(Guid.NewGuid())));
    }

    [SkippableFact]
    public async Task Guests_and_malformed_requests_are_refused()
    {
        using var anonymous = await Host.Anonymous.GetAsync("/api/x/leaderboard/all");
        var (guest, _) = await Host.CreateAnotherGuestClientAsync();
        using var asGuest = await guest.GetAsync("/api/x/leaderboard/all");
        using var badPeriod = await Host.Player.GetAsync("/api/x/leaderboard/all?period=2026-13");
        using var badScope = await Host.Player.GetAsync("/api/x/leaderboard/all?scope=world");

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest],
            [anonymous.StatusCode, asGuest.StatusCode, badPeriod.StatusCode, badScope.StatusCode]);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!LeaderboardReadSupport.IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP fixture did not start.");
        }
    }

    private async Task<long> ScoreAsync(Guid playerId)
    {
        var database = Host.Services.GetRequiredService<ITenantDb>();
        var queries = Host.Services.GetRequiredService<INamedQueries>();
        var result = await database.InTenantAsync(Host.TenantId, transaction =>
            queries.InvokeAsync<long>("friends_ids_test", "leaderboard.score", transaction, playerId, CancellationToken.None), readOnly: true);
        Assert.True(result.Available);
        return result.Value;
    }

    private sealed class FriendsIdsProvider : IFeature
    {
        public static IReadOnlyList<Guid> Friends { get; set; } = [];

        public string Id => "friends_ids_test";

        public FeatureSettings Settings => FeatureSettings.Create(new ProviderSettings(), static _ => []);

        public void Configure(IFeatureBuilder feature)
        {
            feature.Provides<IReadOnlyList<Guid>>("friends.ids", (_, _, _) => Task.FromResult(Friends));
            feature.Uses("leaderboard.score");
        }

        private sealed record ProviderSettings;
    }
}

[Collection("Leaderboard read endpoints")]
public sealed class LeaderboardFriendsUnavailableTests : IAsyncLifetime
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!LeaderboardReadSupport.IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new LeaderboardFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "leaderboard", true);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    /// <summary>AC-6.</summary>
    [SkippableFact]
    public async Task Friends_scope_is_unavailable_when_no_feature_provides_friends_ids()
    {
        Skip.If(!LeaderboardReadSupport.IsPostgresConfigured, "PostgreSQL is not configured.");

        using var response = await _host!.Player.GetAsync("/api/x/leaderboard/all?scope=friends");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unavailable", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }
}

[CollectionDefinition("Leaderboard read endpoints", DisableParallelization = true)]
public sealed class LeaderboardReadEndpointCollection;
