using System.Text.RegularExpressions;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Features;
using Quesshi.Features.Stats;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Infrastructure.Migrations;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

public sealed class StatsProjectionDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Stats_tables_are_owned_and_tenant_isolated()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");

        foreach (var table in StatsTables)
        {
            Assert.True(await ScalarAsync<bool>(owner, "select relrowsecurity from pg_class where oid = @table::regclass", ("table", table)));
            Assert.True(await ScalarAsync<bool>(owner, """
                select exists (
                    select 1 from pg_policies
                    where schemaname = 'public' and tablename = @table and policyname = 'tenant_isolation')
                """, ("table", table)));
            Assert.Equal("owner:stats", await ScalarAsync<string>(owner, "select obj_description(@table::regclass)", ("table", table)));
        }
    }

    [Fact]
    public void Stats_feature_is_discovered_with_its_event_and_cleanup_handlers()
    {
        var registry = FeatureRegistry.Create(FeatureRegistry.Discover(typeof(FeaturesMarker).Assembly));

        Assert.Contains(registry.Features, feature => feature.Id == "stats");
        Assert.Contains(("stats", typeof(SessionEnded)), registry.EventHandlers.Keys);
        Assert.Contains("stats", registry.CleanupHandlers.Keys);
    }

    [Fact]
    public void Stats_migration_has_no_foreign_key_to_another_plugin_table()
    {
        var migration = MigrationRunner.Discover(typeof(FeaturesMarker).Assembly)
            .Single(script => script.Name == "20261022T1800_stats_projection.sql");
        var sql = System.Text.Encoding.UTF8.GetString(migration.Contents.Span);
        var referencedTables = Regex.Matches(sql, @"references\s+([a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.All(referencedTables, table => Assert.Contains(table, new[] { "players", "categories", "questions" }));
    }

    [SkippableFact]
    public async Task Disabled_stats_does_not_queue_a_session_ended_job()
    {
        await using var harness = await CreateHarnessAsync(enabled: false);
        var player = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true);

        await harness.EmitAsync(session);

        Assert.Equal(0L, await harness.CountAsync("select count(*) from jobs where tenant_id = @tenant", ("tenant", harness.Tenant)));
    }

    [SkippableFact]
    public async Task Duplicate_session_ended_delivery_leaves_all_stats_tables_unchanged()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var category = await harness.AddCategoryAsync();
        var question = await harness.AddQuestionAsync(category);
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true, slot: 0);
        await harness.AddItemAsync(session, 0, question, category);
        await harness.AddAnswerAsync(session, 0, player, 0.5F);

        await harness.EmitAsync(session);
        Assert.Equal(1L, await harness.CountAsync("select count(*) from jobs where tenant_id = @tenant and owner = 'core' and kind = 'event'"));
        await harness.RunWorkerAsync();
        var first = await harness.ReadStatsSnapshotAsync();
        Assert.All([first.Results, first.Players, first.Categories, first.Questions], Assert.NotEmpty);
        await harness.RequeueSessionEndedAsync(session);
        await harness.RunWorkerAsync();

        Assert.Equal(first, await harness.ReadStatsSnapshotAsync());
    }

    [SkippableFact]
    public async Task Result_totals_and_streaks_are_independent_of_delivery_order()
    {
        var outcomes = new[] { "win", "loss", "win" };
        var orders = new[]
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
        };
        var observed = new List<(int Played, int Wins, int Losses, int Draws, int Streak, int Best, long Points)>();

        foreach (var order in orders)
        {
            await using var harness = await CreateHarnessAsync();
            var player = await harness.AddPlayerAsync();
            var sessions = new Guid[3];
            for (var index = 0; index < sessions.Length; index++)
            {
                sessions[index] = await harness.AddSessionAsync(
                    player, ranked: true, runBegan: true, outcome: outcomes[index], points: 10,
                    endedAt: new DateTimeOffset(2026, 10, 1 + index, 0, 0, 0, TimeSpan.Zero));
            }

            foreach (var index in order) await harness.EmitAndDeliverAsync(sessions[index]);
            observed.Add(await harness.ReadPlayerStatsAsync(player));
        }

        Assert.All(observed, result => Assert.Equal((3, 2, 1, 0, 1, 1, 30L), result));
    }

    [SkippableFact]
    public async Task A_started_player_run_records_its_own_outcome_and_score()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true, outcome: "loss", points: 42);

        await harness.EmitAsync(session);
        Assert.Equal(1L, await harness.CountAsync("select count(*) from jobs where tenant_id = @tenant and owner = 'core' and kind = 'event'"));
        await harness.RunWorkerAsync();
        await harness.AssertEventCompletedAsync(session);

        Assert.Equal(("loss", 42), await harness.ReadResultAsync(player, session));
    }

    [SkippableFact]
    public async Task A_forfeited_run_that_never_began_records_no_result_or_game()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: false, endReason: "forfeit");

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal(0L, await harness.CountAsync("select count(*) from player_results where player_id = @player", ("player", player)));
    }

    [SkippableFact]
    public async Task A_no_contest_session_contributes_no_stats()
    {
        await AssertExcludedSessionAsync("finished", "no_contest", ranked: false);
    }

    [SkippableFact]
    public async Task A_cancelled_session_contributes_no_stats()
    {
        await AssertExcludedSessionAsync("cancelled", "admin", ranked: false);
    }

    [SkippableFact]
    public async Task An_unscored_session_contributes_no_stats()
    {
        await AssertExcludedSessionAsync("finished", "completed", ranked: false);
    }

    [SkippableFact]
    public async Task Tied_leaders_record_draws_and_the_lower_score_records_a_loss()
    {
        await using var harness = await CreateHarnessAsync();
        var leaders = new[] { await harness.AddPlayerAsync(), await harness.AddPlayerAsync() };
        var third = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(leaders[0], ranked: true, runBegan: true, outcome: "draw", points: 100);
        await harness.AddSeatAsync(session, leaders[1], 1, true, true, "draw", 100);
        await harness.AddSeatAsync(session, third, 2, true, true, "loss", 50);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal((0, 0, 1), await harness.ReadRecordAsync(leaders[0]));
        Assert.Equal((0, 0, 1), await harness.ReadRecordAsync(leaders[1]));
        Assert.Equal((0, 1, 0), await harness.ReadRecordAsync(third));
    }

    [SkippableFact]
    public async Task Category_accuracy_counts_only_recorded_expected_answers_and_misses()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var category = await harness.AddCategoryAsync();
        var question = await harness.AddQuestionAsync(category);
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true, slot: 9, format: "live");
        for (var slot = 0; slot < 10; slot++) await harness.AddItemAsync(session, slot, question, category);
        await harness.AddAnswerAsync(session, 0, player, 1F);
        await harness.AddAnswerAsync(session, 1, player, 0F);
        await harness.AddAnswerAsync(session, 2, player, 0F);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal((3, 1D), await harness.ReadCategoryStatsAsync(player, category));
    }

    [SkippableFact]
    public async Task Question_asked_increases_once_for_each_graded_player_answer_or_miss()
    {
        await using var harness = await CreateHarnessAsync();
        var first = await harness.AddPlayerAsync();
        var second = await harness.AddPlayerAsync();
        var category = await harness.AddCategoryAsync();
        var question = await harness.AddQuestionAsync(category);
        var session = await harness.AddSessionAsync(first, ranked: true, runBegan: true, slot: 0);
        await harness.AddSeatAsync(session, second, 1, true, true, "loss", 0);
        await harness.AddItemAsync(session, 0, question, category);
        await harness.AddAnswerAsync(session, 0, first, 1F);
        await harness.AddAnswerAsync(session, 0, second, 0F);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal(2L, await harness.ReadQuestionAskedAsync(question));
    }

    [SkippableFact]
    public async Task Question_served_increases_once_per_opened_slot()
    {
        await using var harness = await CreateHarnessAsync();
        var first = await harness.AddPlayerAsync();
        var second = await harness.AddPlayerAsync();
        var category = await harness.AddCategoryAsync();
        var question = await harness.AddQuestionAsync(category);
        var session = await harness.AddSessionAsync(first, ranked: true, runBegan: true, slot: 0);
        await harness.AddSeatAsync(session, second, 1, true, true, "loss", 0);
        await harness.AddItemAsync(session, 0, question, category);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal(1L, await harness.ReadQuestionServedAsync(question));
    }

    [SkippableFact]
    public async Task Fractional_credit_is_added_to_category_and_question_accuracy()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var category = await harness.AddCategoryAsync();
        var question = await harness.AddQuestionAsync(category);
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true, slot: 0);
        await harness.AddItemAsync(session, 0, question, category);
        await harness.AddAnswerAsync(session, 0, player, 0.25F);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal((1, 0.25D), await harness.ReadCategoryStatsAsync(player, category));
        Assert.Equal(0.25D, await harness.ReadQuestionCreditAsync(question));
    }

    [SkippableFact]
    public async Task Player_cleanup_removes_player_stats_even_when_stats_is_disabled()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var category = await harness.AddCategoryAsync();
        var question = await harness.AddQuestionAsync(category);
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true, slot: 0);
        await harness.AddItemAsync(session, 0, question, category);
        await harness.AddAnswerAsync(session, 0, player, 1F);
        await harness.EmitAndDeliverAsync(session);
        await harness.SetEnabledAsync(false);
        await harness.DeletePlayerAsync(player);

        await harness.EnqueueCleanupAndDeliverAsync(player);

        Assert.Equal((0L, 0L, 0L), await harness.ReadPlayerTableCountsAsync(player));
    }

    [SkippableFact]
    public async Task A_late_session_event_does_not_recreate_deleted_player_stats()
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(player, ranked: true, runBegan: true);
        await harness.DeletePlayerAsync(player);

        await harness.EmitAndDeliverAsync(session);

        Assert.Equal((0L, 0L, 0L), await harness.ReadPlayerTableCountsAsync(player));
    }

    private async Task AssertExcludedSessionAsync(string status, string? endReason, bool ranked)
    {
        await using var harness = await CreateHarnessAsync();
        var player = await harness.AddPlayerAsync();
        var session = await harness.AddSessionAsync(player, ranked, true, status: status, endReason: endReason);
        await harness.EmitAndDeliverAsync(session);
        Assert.Equal((0L, 0L, 0L, 0L), await harness.ReadStatsTableCountsAsync());
    }

    private async Task<StatsHarness> CreateHarnessAsync(bool enabled = true)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenant = Guid.NewGuid();
        await using (var owner = await OpenAsync("quesshi_owner"))
        {
            await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@tenant, @slug, @slug, 'en')", ("tenant", tenant), ("slug", $"stats-{tenant:N}"));
            await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@tenant, 'en', 'English', 'English')", ("tenant", tenant));
            await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled) values (@tenant, 'feature', 'stats', true, @enabled)", ("tenant", tenant), ("enabled", enabled));
        }
        return new StatsHarness(fixture, tenant);
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static readonly string[] StatsTables = ["player_results", "player_stats", "player_category_stats", "question_stats"];

    private sealed class StatsHarness(PostgresFixture fixture, Guid tenant) : IAsyncDisposable
    {
        private readonly DatabaseSources _sources = new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        private readonly FeatureRegistry _registry = FeatureRegistry.Create([new StatsFeature()]);
        private readonly RecordingNotifier _notifier = new();

        public Guid Tenant { get; } = tenant;

        public async ValueTask DisposeAsync() => await _sources.DisposeAsync();

        public async Task<Guid> AddPlayerAsync()
        {
            var player = Guid.NewGuid();
            await ExecuteOwnerAsync("insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Stats', 'stats', 'en')", ("player", player));
            return player;
        }

        public async Task<Guid> AddCategoryAsync()
        {
            var category = Guid.NewGuid();
            await ExecuteOwnerAsync("insert into categories (tenant_id, id, slug, names) values (@tenant, @category, @slug, '{\"en\":\"Stats\"}'::jsonb)", ("category", category), ("slug", $"stats-{category:N}"));
            return category;
        }

        public async Task<Guid> AddQuestionAsync(Guid category)
        {
            var question = Guid.NewGuid();
            await ExecuteOwnerAsync("""
                insert into questions (tenant_id, id, kind, family, lang, category_id, prompt, payload, status, source)
                values (@tenant, @question, 'choice', 'trivia', 'en', @category, @prompt, '{}'::jsonb, 'approved', 'seed')
                """, ("question", question), ("category", category), ("prompt", question.ToString("N")));
            return question;
        }

        public async Task<Guid> AddSessionAsync(Guid owner, bool ranked, bool runBegan, string outcome = "win", int points = 10, int slot = -1, string status = "finished", string? endReason = "completed", DateTimeOffset? endedAt = null, string format = "async_duel")
        {
            var session = Guid.NewGuid();
            var ended = endedAt ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            await ExecuteOwnerAsync("""
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, state, phase, slot, started_at, ended_at, end_reason)
                values (@tenant, @session, @code, @format, @owner, 'en', @status, 8, '{}'::jsonb, '{}'::jsonb, 'finished', @slot, @ended, @ended, @endReason)
                """, ("session", session), ("code", session.ToString("N")[..8]), ("format", format), ("owner", owner), ("status", status), ("slot", slot), ("ended", ended), ("endReason", endReason ?? (object)DBNull.Value));
            await AddSeatAsync(session, owner, 0, ranked, runBegan, outcome, points);
            return session;
        }

        public Task AddSeatAsync(Guid session, Guid player, short seat, bool ranked, bool runBegan, string outcome, int points, bool eligible = true) =>
            ExecuteOwnerAsync("""
                insert into session_participants (tenant_id, session_id, player_id, seat, role, status, slot, eligible, score, outcome, ranked, result)
                values (@tenant, @session, @player, @seat, 'player', 'done', 0, @eligible, @points, @outcome, @ranked, @result::jsonb)
                """, ("session", session), ("player", player), ("seat", seat), ("points", points), ("outcome", outcome), ("ranked", ranked), ("eligible", eligible), ("result", runBegan ? "{\"runBegan\":true}" : "{}"));

        public Task AddItemAsync(Guid session, int slot, Guid question, Guid category) =>
            ExecuteOwnerAsync("""
                insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, card, reveal, category_id)
                values (@tenant, @session, @slot, @question, 'choice', 1, 'Stats', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, @category)
                """, ("session", session), ("slot", slot), ("question", question), ("category", category));

        public Task AddAnswerAsync(Guid session, int slot, Guid player, float credit) =>
            ExecuteOwnerAsync("""
                insert into session_answers (tenant_id, session_id, slot, player_id, credit, received_at)
                values (@tenant, @session, @slot, @player, @credit, now())
                """, ("session", session), ("slot", slot), ("player", player), ("credit", credit));

        public Task SetEnabledAsync(bool enabled) => ExecuteOwnerAsync("update tenant_capabilities set enabled = @enabled where tenant_id = @tenant and type = 'feature' and key = 'stats'", ("enabled", enabled));

        public Task DeletePlayerAsync(Guid player) => ExecuteOwnerAsync("update players set deleted_at = now() where tenant_id = @tenant and id = @player", ("player", player));

        public Task EmitAsync(Guid session) => new TenantDb(_sources).InTenantAsync(Tenant, async transaction =>
        {
            await Events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });

        public async Task EmitAndDeliverAsync(Guid session)
        {
            await EmitAsync(session);
            await RunWorkerAsync();
            await AssertEventCompletedAsync(session);
        }

        public async Task AssertEventCompletedAsync(Guid session)
        {
            var state = await ReadEventJobStateAsync(session);
            if (state.Status != "done") throw new InvalidOperationException($"SessionEnded job did not finish: {state.Status}; {state.Error}");
        }

        public async Task EnqueueCleanupAndDeliverAsync(Guid player)
        {
            await new TenantDb(_sources).InTenantAsync(Tenant, async transaction =>
            {
                await Events.EnqueueAsync(transaction, player);
                return 0;
            });
            await RunWorkerAsync();
        }

        public Task RequeueSessionEndedAsync(Guid session) => ExecuteOwnerAsync("""
            update jobs set status = 'ready', locked_until = null, run_at = now()
            where tenant_id = @tenant and kind = 'event' and payload->>'subjectId' = @session
            """, ("session", session.ToString()));

        public Task<(string Status, string? Error)> ReadEventJobStateAsync(Guid session) => QueryOwnerAsync("""
            select status, last_error
            from jobs
            where tenant_id = @tenant
              and kind = 'event'
              and payload->>'subjectId' = @session
            """, reader => (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)), ("session", session.ToString()));

        public async Task RunWorkerAsync()
        {
            var handlers = _registry.Jobs;
            if (!handlers.TryGet("core", "event", out _)) handlers.Register("core", "event", Events.HandleEventJobAsync);
            if (!handlers.TryGet("core", "cleanup-player", out _)) handlers.Register("core", "cleanup-player", Events.HandleCleanupJobAsync);
            var worker = new JobWorker(new PostgresJobStore(_sources, _notifier), handlers, new TenantDb(_sources), new OffCapabilities());
            await worker.RunPassAsync(100, TimeSpan.FromMinutes(1));
        }

        public Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) => ScalarOwnerAsync<long>(sql, parameters);

        public async Task<(string Results, string Players, string Categories, string Questions)> ReadStatsSnapshotAsync() =>
            (await ScalarOwnerAsync<string>("select coalesce(string_agg(format('%s|%s|%s|%s|%s|%s', player_id, session_id, format, ended_at, outcome, points), ',' order by player_id, session_id), '') from player_results where tenant_id = @tenant"),
             await ScalarOwnerAsync<string>("select coalesce(string_agg(format('%s|%s|%s|%s|%s|%s|%s|%s', player_id, format, played, wins, losses, draws, streak, best_streak), ',' order by player_id, format), '') from player_stats where tenant_id = @tenant"),
             await ScalarOwnerAsync<string>("select coalesce(string_agg(format('%s|%s|%s|%s', player_id, category_id, asked, credit_sum), ',' order by player_id, category_id), '') from player_category_stats where tenant_id = @tenant"),
             await ScalarOwnerAsync<string>("select coalesce(string_agg(format('%s|%s|%s|%s', question_id, asked, served, credit_sum), ',' order by question_id), '') from question_stats where tenant_id = @tenant"));

        public async Task<(long Results, long Players, long Categories, long Questions)> ReadStatsTableCountsAsync() =>
            (await CountAsync("select count(*) from player_results where tenant_id = @tenant"), await CountAsync("select count(*) from player_stats where tenant_id = @tenant"), await CountAsync("select count(*) from player_category_stats where tenant_id = @tenant"), await CountAsync("select count(*) from question_stats where tenant_id = @tenant"));

        public Task<(string Outcome, int Points)> ReadResultAsync(Guid player, Guid session) => QueryOwnerAsync("select outcome, points from player_results where tenant_id = @tenant and player_id = @player and session_id = @session", reader => (reader.GetString(0), reader.GetInt32(1)), ("player", player), ("session", session));

        public Task<(int Played, int Wins, int Losses, int Draws, int Streak, int Best, long Points)> ReadPlayerStatsAsync(Guid player) => QueryOwnerAsync("select played, wins, losses, draws, streak, best_streak, points from player_stats where tenant_id = @tenant and player_id = @player", reader => (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6)), ("player", player));

        public Task<(int Wins, int Losses, int Draws)> ReadRecordAsync(Guid player) => QueryOwnerAsync("select wins, losses, draws from player_stats where tenant_id = @tenant and player_id = @player", reader => (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)), ("player", player));

        public Task<(int Asked, double Credit)> ReadCategoryStatsAsync(Guid player, Guid category) => QueryOwnerAsync("select asked, credit_sum from player_category_stats where tenant_id = @tenant and player_id = @player and category_id = @category", reader => (reader.GetInt32(0), reader.GetDouble(1)), ("player", player), ("category", category));

        public Task<long> ReadQuestionAskedAsync(Guid question) => ScalarOwnerAsync<long>("select asked from question_stats where tenant_id = @tenant and question_id = @question", ("question", question));

        public Task<long> ReadQuestionServedAsync(Guid question) => ScalarOwnerAsync<long>("select served from question_stats where tenant_id = @tenant and question_id = @question", ("question", question));

        public Task<double> ReadQuestionCreditAsync(Guid question) => ScalarOwnerAsync<double>("select credit_sum from question_stats where tenant_id = @tenant and question_id = @question", ("question", question));

        public async Task<(long Results, long Players, long Categories)> ReadPlayerTableCountsAsync(Guid player) =>
            (await ScalarOwnerAsync<long>("select count(*) from player_results where tenant_id = @tenant and player_id = @player", ("player", player)), await ScalarOwnerAsync<long>("select count(*) from player_stats where tenant_id = @tenant and player_id = @player", ("player", player)), await ScalarOwnerAsync<long>("select count(*) from player_category_stats where tenant_id = @tenant and player_id = @player", ("player", player)));

        private DurableEvents Events => new(_registry, new TenantJobs(), new TenantLocks(), _notifier);

        private async Task ExecuteOwnerAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            await ExecuteAsync(owner, sql, [("tenant", Tenant), .. parameters]);
        }

        private async Task<T> ScalarOwnerAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            return await ScalarAsync<T>(owner, sql, [("tenant", Tenant), .. parameters]);
        }

        private async Task<T> QueryOwnerAsync<T>(string sql, Func<NpgsqlDataReader, T> read, params (string Name, object Value)[] parameters)
        {
            await using var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
            await owner.OpenAsync();
            await using var command = new NpgsqlCommand(sql, owner);
            command.Parameters.AddWithValue("tenant", Tenant);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return read(reader);
        }

        private sealed class RecordingNotifier : ISessionEffectsNotifier
        {
            public void Changed(ITenantTx transaction, Guid sessionId) { }
        }

        private sealed class OffCapabilities : ICapabilities
        {
            public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();

            public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default) where TSettings : notnull => throw new NotSupportedException();
        }
    }
}
