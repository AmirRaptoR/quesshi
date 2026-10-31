using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Features;
using Quesshi.Features.Leaderboard;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class LeaderboardLedgerDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static DateTimeOffset At(int month, int day, int hour = 10) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Leaderboard_feature_is_discovered_with_its_event_and_cleanup_handlers()
    {
        var registry = FeatureRegistry.Create(FeatureRegistry.Discover(typeof(FeaturesMarker).Assembly));

        Assert.Contains(registry.Features, feature => feature.Id == "leaderboard");
        Assert.Contains(("leaderboard", typeof(SessionEnded)), registry.EventHandlers.Keys);
        Assert.Contains("leaderboard", registry.CleanupHandlers.Keys);
    }

    [SkippableFact]
    public async Task Tables_are_owned_by_leaderboard_and_the_top_index_exists()
    {
        await using var harness = await CreateHarnessAsync();

        foreach (var table in new[] { "leaderboard_abandonments", "leaderboard_entries", "leaderboard_scores" })
        {
            Assert.Equal("owner:leaderboard", await harness.ScalarAsync<string>("select obj_description(@table::regclass)", ("table", table)));
        }
        Assert.Equal(1L, await harness.ScalarAsync<long>("select count(*) from pg_indexes where indexname = 'leaderboard_top'"));
    }

    /// <summary>AC-1.</summary>
    [SkippableFact]
    public async Task Five_sessions_applied_in_any_order_give_identical_scores_and_penalties()
    {
        var random = new Random(430);
        var orders = new List<int[]> { new[] { 0, 1, 2, 3, 4 }, new[] { 4, 3, 2, 1, 0 } };
        while (orders.Count < 24)
        {
            orders.Add(Enumerable.Range(0, 5).OrderBy(_ => random.Next()).ToArray());
        }

        string? expected = null;
        foreach (var order in orders)
        {
            await using var harness = await CreateHarnessAsync();
            var player = await harness.AddPlayerAsync();
            var sessions = new[]
            {
                await harness.AddSessionAsync(At(10, 30), [Seat(player, score: 300)]),
                await harness.AddSessionAsync(At(10, 31), [Seat(player, abandoned: true)]),
                await harness.AddSessionAsync(At(11, 1), [Seat(player, abandoned: true)]),
                await harness.AddSessionAsync(At(11, 2), [Seat(player, score: 100)], format: "async_duel"),
                await harness.AddSessionAsync(At(11, 3), [Seat(player, abandoned: true)])
            };

            foreach (var index in order) await harness.EmitAndDeliverAsync(sessions[index]);

            var snapshot = await harness.SnapshotAsync();
            expected ??= snapshot;
            Assert.Equal(expected, snapshot);
            Assert.Equal(new[] { 0, 200, 400 }, await harness.AbandonmentAmountsAsync(player));
            Assert.Equal(0L, await harness.ScoreAsync(player, "all", "all"));
            Assert.Equal(0L, await harness.ScoreAsync(player, "live", "all"));
        }
    }

    /// <summary>AC-2 and AC-3.</summary>
    [SkippableFact]
    public async Task A_penalty_floors_the_score_at_zero_and_forgives_the_unpaid_part()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var free = await harness.AddSessionAsync(At(10, 1), [Seat(player, abandoned: true)]);
        var earned = await harness.AddSessionAsync(At(10, 2), [Seat(player, score: 100)]);
        var costly = await harness.AddSessionAsync(At(10, 3), [Seat(player, abandoned: true)]);
        var later = await harness.AddSessionAsync(At(10, 4), [Seat(player, score: 100)]);

        await harness.EmitAndDeliverAsync(free);
        Assert.Equal(0L, await harness.ScoreAsync(player, "all", "all"));
        await harness.EmitAndDeliverAsync(earned);
        Assert.Equal(100L, await harness.ScoreAsync(player, "all", "all"));
        await harness.EmitAndDeliverAsync(costly);
        Assert.Equal(0L, await harness.ScoreAsync(player, "all", "all"));
        await harness.EmitAndDeliverAsync(later);
        Assert.Equal(100L, await harness.ScoreAsync(player, "all", "all"));
    }

    /// <summary>AC-4.</summary>
    [SkippableFact]
    public async Task Abandonments_one_to_six_within_seven_days_cost_the_fixed_tiers()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        for (var day = 0; day < 6; day++)
        {
            await harness.EmitAndDeliverAsync(await harness.AddSessionAsync(At(10, 1 + day), [Seat(player, abandoned: true)]));
        }

        Assert.Equal(new[] { 0, 200, 400, 800, 1000, 1000 }, await harness.AbandonmentAmountsAsync(player));
        Assert.Equal(new[] { 0, 200, 400, 800, 1000, 1000 }, await harness.PenaltyEntryAmountsAsync(player, "all", "all"));
    }

    /// <summary>AC-5.</summary>
    [SkippableFact]
    public async Task A_seventh_abandonment_after_eight_quiet_days_costs_nothing()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        for (var day = 0; day < 6; day++)
        {
            await harness.EmitAndDeliverAsync(await harness.AddSessionAsync(At(10, 1 + day), [Seat(player, abandoned: true)]));
        }

        await harness.EmitAndDeliverAsync(await harness.AddSessionAsync(At(10, 14), [Seat(player, abandoned: true)]));

        Assert.Equal(new[] { 0, 200, 400, 800, 1000, 1000, 0 }, await harness.AbandonmentAmountsAsync(player));
    }

    /// <summary>AC-6.</summary>
    [SkippableFact]
    public async Task A_window_spanning_a_week_and_a_month_boundary_counts_the_earlier_abandonment()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var october = await harness.AddSessionAsync(At(10, 31, 23), [Seat(player, abandoned: true)]);
        var november = await harness.AddSessionAsync(At(11, 2, 1), [Seat(player, abandoned: true)]);

        await harness.EmitAndDeliverAsync(october);
        await harness.EmitAndDeliverAsync(november);

        Assert.Equal(new[] { 0, 200 }, await harness.AbandonmentAmountsAsync(player));
        Assert.Equal(new[] { "2026-10", "2026-11", "2026-W44", "2026-W45", "all" }, await harness.PeriodsAsync(player));
        Assert.Equal(new[] { 200 }, await harness.PenaltyEntryAmountsAsync(player, "all", "2026-W45"));
        Assert.Equal(new[] { 200 }, await harness.PenaltyEntryAmountsAsync(player, "live", "2026-11"));
    }

    [SkippableFact]
    public async Task Periods_follow_the_end_time_in_the_sessions_own_time_zone()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(At(10, 31, 21), [Seat(player, score: 70)], endedTz: "Asia/Tehran");

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal(new[] { "2026-11", "2026-W44", "all" }, await harness.PeriodsAsync(player));
        Assert.Equal(new[] { "all", "live" }, await harness.BoardsAsync(player));
    }

    /// <summary>AC-7.</summary>
    [SkippableFact]
    public async Task A_guest_started_seat_earns_no_points_and_no_penalty()
    {
        await using var harness = await CreateHarnessAsync();
        var scorer = await harness.AddPlayerAsync();
        var quitter = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(At(10, 1), [
            Seat(scorer, score: 90, eligible: false), Seat(quitter, abandoned: true, eligible: false)]);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal((0L, 0L, 0L), await harness.CountsAsync());
    }

    /// <summary>AC-8.</summary>
    [SkippableTheory]
    [InlineData("fault")]
    [InlineData("outage")]
    [InlineData("admin")]
    [InlineData("owner")]
    [InlineData("expired")]
    public async Task A_session_ended_by_core_changes_no_leaderboard_row(string reason)
    {
        await using var harness = await CreateHarnessAsync();
        var scorer = await harness.AddPlayerAsync();
        var quitter = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(At(10, 1), [Seat(scorer, score: 90), Seat(quitter, abandoned: true)], endReason: reason);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal((0L, 0L, 0L), await harness.CountsAsync());
    }

    /// <summary>AC-9.</summary>
    [SkippableFact]
    public async Task An_abandoner_in_a_session_others_completed_is_penalised_and_gets_no_points()
    {
        await using var harness = await CreateHarnessAsync();
        var finisher = await harness.AddPlayerAsync();
        var quitter = await harness.AddPlayerAsync();
        var first = await harness.AddSessionAsync(At(10, 1), [Seat(quitter, abandoned: true)]);
        var second = await harness.AddSessionAsync(At(10, 2), [Seat(finisher, score: 80), Seat(quitter, score: 50, abandoned: true)]);

        await harness.EmitAndDeliverAsync(first);
        await harness.EmitAndDeliverAsync(second);

        Assert.Equal(new[] { 0, 200 }, await harness.AbandonmentAmountsAsync(quitter));
        Assert.Equal(0L, await harness.CountAsync("select count(*) from leaderboard_entries where tenant_id = @tenant and player_id = @p and kind = 'points'", ("p", quitter)));
        Assert.Equal(80L, await harness.ScoreAsync(finisher, "all", "all"));
        Assert.Equal(0L, await harness.CountAsync("select count(*) from leaderboard_abandonments where tenant_id = @tenant and player_id = @p", ("p", finisher)));
    }

    [SkippableFact]
    public async Task A_late_earlier_abandonment_raises_the_penalty_of_a_later_one_and_refolds_the_score()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var points = await harness.AddSessionAsync(At(10, 1), [Seat(player, score: 1000)]);
        var second = await harness.AddSessionAsync(At(10, 3), [Seat(player, abandoned: true)]);
        var first = await harness.AddSessionAsync(At(10, 2), [Seat(player, abandoned: true)]);

        await harness.EmitAndDeliverAsync(points);
        await harness.EmitAndDeliverAsync(second);
        Assert.Equal(new[] { 0 }, await harness.AbandonmentAmountsAsync(player));
        Assert.Equal(1000L, await harness.ScoreAsync(player, "all", "all"));
        await harness.EmitAndDeliverAsync(first);

        Assert.Equal(new[] { 0, 200 }, await harness.AbandonmentAmountsAsync(player));
        Assert.Equal(new[] { 0, 200 }, await harness.PenaltyEntryAmountsAsync(player, "live", "2026-10"));
        Assert.Equal(800L, await harness.ScoreAsync(player, "all", "all"));
    }

    [SkippableFact]
    public async Task Redelivering_an_event_changes_nothing()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var points = await harness.AddSessionAsync(At(10, 1), [Seat(player, score: 100)]);
        var quit = await harness.AddSessionAsync(At(10, 2), [Seat(player, abandoned: true)]);
        await harness.EmitAndDeliverAsync(points);
        await harness.EmitAndDeliverAsync(quit);
        var before = await harness.SnapshotAsync();

        await harness.RequeueAsync(points);
        await harness.RequeueAsync(quit);
        await harness.RunWorkerAsync();

        Assert.Equal(before, await harness.SnapshotAsync());
    }

    /// <summary>AC-10.</summary>
    [SkippableFact]
    public async Task Cleanup_removes_every_row_of_the_player()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var other = await harness.AddPlayerAsync();
        await harness.EmitAndDeliverAsync(await harness.AddSessionAsync(At(10, 1), [Seat(player, score: 100), Seat(other, score: 40)]));
        await harness.EmitAndDeliverAsync(await harness.AddSessionAsync(At(10, 2), [Seat(player, abandoned: true)]));
        Assert.NotEqual((0L, 0L, 0L), await harness.CountsAsync(player));
        await harness.DeletePlayerAsync(player);

        await harness.CleanupAndDeliverAsync(player);

        Assert.Equal((0L, 0L, 0L), await harness.CountsAsync(player));
        Assert.Equal(40L, await harness.ScoreAsync(other, "all", "all"));
    }

    /// <summary>AC-11.</summary>
    [SkippableFact]
    public async Task A_session_event_for_a_deleted_player_after_cleanup_creates_no_row()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var points = await harness.AddSessionAsync(At(10, 1), [Seat(player, score: 100)]);
        var quit = await harness.AddSessionAsync(At(10, 2), [Seat(player, abandoned: true)]);
        await harness.DeletePlayerAsync(player);
        await harness.CleanupAndDeliverAsync(player);

        await harness.EmitAndDeliverAsync(points);
        await harness.EmitAndDeliverAsync(quit);

        Assert.Equal((0L, 0L, 0L), await harness.CountsAsync());
    }

    private static SeatSpec Seat(Guid player, int score = 0, bool abandoned = false, bool eligible = true) =>
        new(player, score, abandoned, eligible, Ranked: !abandoned || score > 0);

    private async Task<LeaderboardHarness> CreateHarnessAsync()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenant = Guid.NewGuid();
        await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await Execute(owner, "insert into tenants (id, slug, name, default_language) values (@tenant, @slug, @slug, 'en')", ("tenant", tenant), ("slug", $"lb-{tenant:N}"));
        await Execute(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
        await Execute(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled) values (@tenant, 'feature', 'leaderboard', true, true)", ("tenant", tenant));
        return new LeaderboardHarness(fixture, tenant);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SeatSpec(Guid Player, int Score, bool Abandoned, bool Eligible, bool Ranked);

    private sealed class LeaderboardHarness(PostgresFixture fixture, Guid tenant) : IAsyncDisposable
    {
        private readonly DatabaseSources _sources = new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        private readonly FeatureRegistry _registry = FeatureRegistry.Create([new LeaderboardFeature()]);
        private readonly NoNotifier _notifier = new();

        public Guid Tenant { get; } = tenant;

        public async ValueTask DisposeAsync() => await _sources.DisposeAsync();

        public async Task<Guid> AddPlayerAsync()
        {
            var player = Guid.NewGuid();
            await ExecuteAsync("insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Board', 'board', 'en')", ("player", player));
            return player;
        }

        public async Task<Guid> AddSessionAsync(DateTimeOffset endedAt, IReadOnlyList<SeatSpec> seats, string format = "live", string endReason = "completed", string endedTz = "UTC")
        {
            var session = Guid.NewGuid();
            var status = endReason is "completed" or "forfeit" or "no_contest" or "fault" or "outage" ? "finished" : "cancelled";
            await ExecuteAsync("""
                insert into sessions (tenant_id, id, code, format, owner_id, lang, scored, status, capacity, content, state, phase, slot, started_at, ended_at, end_reason, ended_tz)
                values (@tenant, @session, @code, @format, @owner, 'en', true, @status, 8, '{}'::jsonb, '{}'::jsonb, 'finished', -1, @ended, @ended, @reason, @tz)
                """, ("session", session), ("code", session.ToString("N")[..8]), ("format", format), ("owner", seats[0].Player), ("status", status), ("ended", endedAt), ("reason", endReason), ("tz", endedTz));
            for (var index = 0; index < seats.Count; index++)
            {
                var seat = seats[index];
                await ExecuteAsync("""
                    insert into session_participants (tenant_id, session_id, player_id, seat, role, status, slot, eligible, score, outcome, ranked, result)
                    values (@tenant, @session, @player, @seat, 'player', 'done', 0, @eligible, @score, 'none', @ranked, @result::jsonb)
                    """, ("session", session), ("player", seat.Player), ("seat", (short)index), ("eligible", seat.Eligible), ("score", seat.Score), ("ranked", seat.Ranked),
                    ("result", seat.Abandoned ? "{\"abandoned\":true}" : "{}"));
            }
            return session;
        }

        public Task DeletePlayerAsync(Guid player) => ExecuteAsync("update players set deleted_at = now() where tenant_id = @tenant and id = @player", ("player", player));

        public async Task EmitAndDeliverAsync(Guid session)
        {
            await new TenantDb(_sources).InTenantAsync(Tenant, async transaction =>
            {
                await Events.EmitAsync(transaction, new SessionEnded(session));
                return 0;
            });
            await RunWorkerAsync();
            var state = await ScalarAsync<string>("select status from jobs where tenant_id = @tenant and kind = 'event' and payload->>'subjectId' = @session", ("session", session.ToString()));
            if (state != "done") throw new InvalidOperationException($"SessionEnded job did not finish: {state}");
        }

        public async Task CleanupAndDeliverAsync(Guid player)
        {
            await new TenantDb(_sources).InTenantAsync(Tenant, async transaction =>
            {
                await Events.EnqueueAsync(transaction, player);
                return 0;
            });
            await RunWorkerAsync();
        }

        public Task RequeueAsync(Guid session) => ExecuteAsync("""
            update jobs set status = 'ready', locked_until = null, run_at = now()
            where tenant_id = @tenant and kind = 'event' and payload->>'subjectId' = @session
            """, ("session", session.ToString()));

        public async Task RunWorkerAsync()
        {
            var handlers = _registry.Jobs;
            if (!handlers.TryGet("core", "event", out _)) handlers.Register("core", "event", Events.HandleEventJobAsync);
            if (!handlers.TryGet("core", "cleanup-player", out _)) handlers.Register("core", "cleanup-player", Events.HandleCleanupJobAsync);
            var worker = new JobWorker(new PostgresJobStore(_sources, _notifier), handlers, new TenantDb(_sources), new OffCapabilities());
            await worker.RunPassAsync(100, TimeSpan.FromMinutes(1));
        }

        public async Task<int[]> AbandonmentAmountsAsync(Guid player) => await ListAsync(
            "select amount from leaderboard_abandonments where tenant_id = @tenant and player_id = @player order by ended_at, session_id", r => r.GetInt32(0), ("player", player));

        public async Task<int[]> PenaltyEntryAmountsAsync(Guid player, string board, string period) => await ListAsync(
            "select amount from leaderboard_entries where tenant_id = @tenant and player_id = @player and board = @board and period = @period and kind = 'penalty' order by at, session_id",
            r => r.GetInt32(0), ("player", player), ("board", board), ("period", period));

        public async Task<string[]> PeriodsAsync(Guid player) => await ListAsync(
            "select period from (select distinct period from leaderboard_entries where tenant_id = @tenant and player_id = @player) p order by period collate \"C\"", r => r.GetString(0), ("player", player));

        public async Task<string[]> BoardsAsync(Guid player) => await ListAsync(
            "select board from (select distinct board from leaderboard_entries where tenant_id = @tenant and player_id = @player) b order by board collate \"C\"", r => r.GetString(0), ("player", player));

        public Task<long> ScoreAsync(Guid player, string board, string period) => ScalarAsync<long>(
            "select score from leaderboard_scores where tenant_id = @tenant and player_id = @player and board = @board and period = @period",
            ("player", player), ("board", board), ("period", period));

        public async Task<(long Abandonments, long Entries, long Scores)> CountsAsync(Guid? player = null)
        {
            var filter = player is null ? "" : " and player_id = @player";
            var parameters = player is null ? Array.Empty<(string, object)>() : [("player", (object)player.Value)];
            return (await CountAsync($"select count(*) from leaderboard_abandonments where tenant_id = @tenant{filter}", parameters),
                    await CountAsync($"select count(*) from leaderboard_entries where tenant_id = @tenant{filter}", parameters),
                    await CountAsync($"select count(*) from leaderboard_scores where tenant_id = @tenant{filter}", parameters));
        }

        public Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) => ScalarAsync<long>(sql, parameters);

        public async Task<string> SnapshotAsync() => string.Join('\n',
            await ScalarAsync<string>("select coalesce(string_agg(format('%s|%s|%s', ended_at, ended_tz, amount), ',' order by ended_at), '') from leaderboard_abandonments where tenant_id = @tenant"),
            await ScalarAsync<string>("select coalesce(string_agg(format('%s|%s|%s|%s|%s', board, period, kind, at, amount), ',' order by board, period, at, kind), '') from leaderboard_entries where tenant_id = @tenant"),
            await ScalarAsync<string>("select coalesce(string_agg(format('%s|%s|%s|%s', board, period, score, last_at), ',' order by board, period), '') from leaderboard_scores where tenant_id = @tenant"));

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var owner = await OpenOwnerAsync();
            await using var command = Command(owner, sql, parameters);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        private async Task<T[]> ListAsync<T>(string sql, Func<NpgsqlDataReader, T> read, params (string Name, object Value)[] parameters)
        {
            await using var owner = await OpenOwnerAsync();
            await using var command = Command(owner, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<T>();
            while (await reader.ReadAsync()) result.Add(read(reader));
            return [.. result];
        }

        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var owner = await OpenOwnerAsync();
            await using var command = Command(owner, sql, parameters);
            await command.ExecuteNonQueryAsync();
        }

        private NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
        {
            var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("tenant", Tenant);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command;
        }

        private async Task<NpgsqlConnection> OpenOwnerAsync()
        {
            var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await connection.OpenAsync();
            return connection;
        }

        private DurableEvents Events => new(_registry, new TenantJobs(), new TenantLocks(), _notifier);

        private sealed class NoNotifier : ISessionEffectsNotifier
        {
            public Task ChangedAsync(ITenantTx transaction, Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class OffCapabilities : ICapabilities
        {
            public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();

            public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
        }
    }
}
