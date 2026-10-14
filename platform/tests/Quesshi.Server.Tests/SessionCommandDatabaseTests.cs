using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Orleans;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Shared;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SessionCommandDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Snapshot_loader_finds_later_insertions_and_keeps_historical_answers_in_a_bounded_open_snapshot()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var ownerId = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertPlayersAsync(owner, tenant, 500);

        await using var sources = Sources();
        var database = new CountingTenantDatabase(new TenantDb(sources));
        var loader = new SessionSnapshotLoader(database);
        Assert.Null(await loader.LoadAsync(tenant, session));

        await InsertRunningSessionAsync(owner, tenant, session, ownerId, 100);
        await InsertParticipantsAsync(owner, tenant, session, 500);
        await InsertItemsAsync(owner, tenant, session, 100);
        await InsertAnswersAsync(owner, tenant, session, 500);
        await ExecuteAsync(owner, "update sessions set slot = 1 where tenant_id = @tenant and id = @session", ("tenant", tenant), ("session", session));
        await ExecuteAsync(owner, "update session_participants set slot = 1 where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session));

        database.Reset();
        var snapshot = Assert.IsType<SessionState>(await loader.LoadAsync(tenant, session));
        Assert.Equal(500, snapshot.Seats.Count);
        Assert.Equal(100, snapshot.ItemCount);
        Assert.Equal([1], snapshot.Items.Keys);
        Assert.Equal(500, snapshot.Answers.Count);
        Assert.All(snapshot.Answers.Values, answer => Assert.NotNull(answer.Hash));
        Assert.Equal(System.Data.IsolationLevel.RepeatableRead, database.IsolationLevel);
        Assert.True(database.ReadOnly);
        Assert.Equal(4, database.QueryCount);

        var next = Assert.Single(await loader.LoadItemsAsync(tenant, session, [1]));
        Assert.Equal(1, next.Metadata.Slot);
    }

    [SkippableFact]
    public async Task Roster_pages_all_seats_in_order_and_hides_unseated_or_cross_tenant_sessions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var viewer = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertTenantAsync(owner, otherTenant);
        await InsertPlayersAsync(owner, tenant, 500);
        await InsertRunningSessionAsync(owner, tenant, session, viewer, 1);
        await InsertParticipantsAsync(owner, tenant, session, 500);

        await using var sources = Sources();
        var database = new CountingTenantDatabase(new TenantDb(sources));
        var loader = new SessionSnapshotLoader(database);
        var ids = new List<string>();
        int? after = null;
        do
        {
            var page = Assert.IsType<RosterPage>(await loader.LoadRosterPageAsync(tenant, session, viewer.ToString(), after, 73));
            ids.AddRange(page.Seats.Select(seat => seat.Id));
            after = page.NextSeat;
        } while (after is not null);

        Assert.Equal(Enumerable.Range(1, 500).Select(number => PlayerId(number).ToString()), ids);
        Assert.Equal(500, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Null(await loader.LoadRosterPageAsync(tenant, session, Guid.NewGuid().ToString(), null, 50));
        Assert.Null(await loader.LoadRosterPageAsync(otherTenant, session, viewer.ToString(), null, 50));
        Assert.Equal(System.Data.IsolationLevel.RepeatableRead, database.IsolationLevel);
        Assert.True(database.ReadOnly);
    }

    [SkippableFact]
    public async Task Version_fence_persists_one_answer_revision_and_notifies_the_committed_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertPlayersAsync(owner, tenant, 1);
        await InsertRunningSessionAsync(owner, tenant, session, player, 1);
        await InsertParticipantsAsync(owner, tenant, session, 1);
        await InsertItemsAsync(owner, tenant, session, 1);

        await using var sources = Sources();
        var loader = new SessionSnapshotLoader(new TenantDb(sources));
        var persisted = Assert.IsType<SessionState>(await loader.LoadAsync(tenant, session));
        var store = new SessionStateStore(new TenantDb(sources));
        var candidate = WithAnswer(persisted, player, revision: 1, answer: "first");

        await using var listener = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await listener.OpenAsync();
        var notification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        listener.Notification += (_, args) =>
        {
            notificationCount++;
            notification.TrySetResult(args.Payload);
        };
        await ExecuteAsync(listener, "listen quesshi_session");
        var wait = listener.WaitAsync(CancellationToken.None);

        var writes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => TrySaveAsync(store, tenant, session, candidate)));
        Assert.Equal(1, Assert.Single(writes, version => version > 0));
        Assert.Equal(1, writes.Count(version => version == -1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal($"{tenant}:{session}:1:0", await notification.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, notificationCount);

        var saved = Assert.IsType<SessionState>(await loader.LoadAsync(tenant, session));
        var answer = Assert.Single(saved.Answers);
        Assert.Equal(1, saved.Session.Version);
        Assert.Equal(1, answer.Value.Revision);
        Assert.Equal("first", answer.Value.Stored?.GetProperty("value").GetString());

        var revised = WithAnswer(saved, player, revision: 2, answer: "second");
        Assert.Equal(2, await store.SaveAsync(tenant, session, saved.Session.Version, revised));
        var rows = await ScalarAsync<long>(owner, "select count(*) from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session));
        var revision = await ScalarAsync<int>(owner, "select revision from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session));
        var storedAnswer = await ScalarAsync<string>(owner, "select answer ->> 'value' from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session));
        Assert.Equal(1, rows);
        Assert.Equal(2, revision);
        Assert.Equal("second", storedAnswer);
    }

    [SkippableFact]
    public async Task State_store_updates_only_rows_changed_by_a_500_seat_immutable_transition()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var first = PlayerId(1);
        var untouched = PlayerId(2);
        await InsertTenantAsync(owner, tenant);
        await InsertPlayersAsync(owner, tenant, 500);
        await InsertRunningSessionAsync(owner, tenant, session, first, 1);
        await InsertParticipantsAsync(owner, tenant, session, 500);
        await InsertItemsAsync(owner, tenant, session, 1);
        await InsertAnswersAsync(owner, tenant, session, 500);

        await using var sources = Sources();
        var loader = new SessionSnapshotLoader(new TenantDb(sources));
        var initial = Assert.IsType<SessionState>(await loader.LoadAsync(tenant, session));
        var changedAnswerVersion = await XminAsync(owner, "session_answers", tenant, session, first);
        var untouchedAnswerVersion = await XminAsync(owner, "session_answers", tenant, session, untouched);
        var changedSeatVersion = await XminAsync(owner, "session_participants", tenant, session, first);
        var untouchedSeatVersion = await XminAsync(owner, "session_participants", tenant, session, untouched);

        var changedAnswers = new Dictionary<AnswerKey, AnswerRecord>(initial.Answers)
        {
            [new(first.ToString(), 0)] = initial.Answers[new(first.ToString(), 0)] with { Revision = 2 },
        };
        var changedSeats = new Dictionary<string, Seat>(initial.Seats)
        {
            [first.ToString()] = initial.Seats[first.ToString()] with
            {
                Score = 9,
                Position = new Position(0, 1),
                Status = SeatStatus.Done,
            },
        };
        var current = initial with
        {
            Answers = SessionState.ReadOnly(changedAnswers),
            Seats = SessionState.ReadOnly(changedSeats),
        };

        var store = new SessionStateStore(new TenantDb(sources));
        Assert.Equal(1, await store.SaveAsync(tenant, session, initial.Session.Version, initial, current));

        Assert.Equal(untouchedAnswerVersion, await XminAsync(owner, "session_answers", tenant, session, untouched));
        Assert.Equal(untouchedSeatVersion, await XminAsync(owner, "session_participants", tenant, session, untouched));
        Assert.NotEqual(changedAnswerVersion, await XminAsync(owner, "session_answers", tenant, session, first));
        Assert.NotEqual(changedSeatVersion, await XminAsync(owner, "session_participants", tenant, session, first));
        Assert.Equal(9, await ScalarAsync<int>(owner, "select score from session_participants where tenant_id = @tenant and session_id = @session and player_id = @player", ("tenant", tenant), ("session", session), ("player", first)));
        Assert.Equal(1, await ScalarAsync<int>(owner, "select slot from session_participants where tenant_id = @tenant and session_id = @session and player_id = @player", ("tenant", tenant), ("session", session), ("player", first)));
        Assert.Equal("done", await ScalarAsync<string>(owner, "select status from session_participants where tenant_id = @tenant and session_id = @session and player_id = @player", ("tenant", tenant), ("session", session), ("player", first)));
    }

    [SkippableFact]
    public async Task Real_session_grain_does_not_cache_missing_sessions_or_cross_tenant_state()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertTenantAsync(owner, otherTenant);
        await InsertPlayersAsync(owner, tenant, 2);

        await using var app = Program.CreateApplication(Sources(), 11131, 30131);
        app.Urls.Add("http://127.0.0.1:50131");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            var factory = app.Services.GetRequiredService<IGrainFactory>();
            var command = new SessionCommand(player.ToString(), "answer", "0", 0, 0, 0, null, "{\"option\":\"a\"}", DateTimeOffset.UtcNow);

            var missing = factory.GetGrain<ISessionGrain>($"{tenant}/{session}");
            Assert.Equal("not_found", (await missing.ExecuteAsync(command)).Code);

            await InsertRunningSessionAsync(owner, tenant, session, player, 1, format: "poll");
            await InsertParticipantsAsync(owner, tenant, session, 2);
            await InsertPollChoiceItemAsync(owner, tenant, session);

            // The same key reactivates and loads the inserted row, proving the prior absence
            // was not cached.
            Assert.Equal("ok", (await missing.ExecuteAsync(command)).Code);
            Assert.Equal("absent_seat", (await missing.ExecuteAsync(command with { PlayerId = Guid.NewGuid().ToString() })).Code);

            var isolated = factory.GetGrain<ISessionGrain>($"{otherTenant}/{session}");
            Assert.Equal("not_found", (await isolated.ExecuteAsync(command)).Code);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [SkippableFact]
    public async Task Real_poll_grain_persists_revision_replacement_and_keeps_current_views()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertPlayersAsync(owner, tenant, 2);
        await InsertRunningSessionAsync(owner, tenant, session, player, 1, format: "poll");
        await InsertParticipantsAsync(owner, tenant, session, 2);
        await InsertPollChoiceItemAsync(owner, tenant, session);

        await using var app = Program.CreateApplication(Sources(), 11132, 30132);
        app.Urls.Add("http://127.0.0.1:50132");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        try
        {
            var grain = app.Services.GetRequiredService<IGrainFactory>().GetGrain<ISessionGrain>($"{tenant}/{session}");
            var received = DateTimeOffset.UtcNow;
            var first = await grain.ExecuteAsync(new SessionCommand(player.ToString(), "answer", "0", 0, 0, 1, null, "{\"option\":\"a\"}", received));
            var retry = await grain.ExecuteAsync(new SessionCommand(player.ToString(), "answer", "0", 0, 0, 1, null, "{\"option\":\"a\"}", received));
            var conflict = await grain.ExecuteAsync(new SessionCommand(player.ToString(), "answer", "0", 0, 0, 1, null, "{\"option\":\"b\"}", received));
            var lower = await grain.ExecuteAsync(new SessionCommand(player.ToString(), "answer", "0", 0, 0, 0, null, "{\"option\":\"b\"}", received));
            var replacement = await grain.ExecuteAsync(new SessionCommand(player.ToString(), "answer", "0", 0, 0, 2, null, "{\"option\":\"b\"}", received));

            Assert.All(new[] { first, retry, conflict, lower, replacement }, result => Assert.NotNull(result.ViewJson));
            Assert.Equal("ok", first.Code);
            Assert.Equal("ok", retry.Code);
            Assert.Equal("revision_conflict", conflict.Code);
            Assert.Equal("ok", lower.Code);
            Assert.Equal("ok", replacement.Code);
            using var view = JsonDocument.Parse(first.ViewJson!);
            Assert.Equal(session.ToString(), view.RootElement.GetProperty("Id").GetString());
            Assert.Equal(1, view.RootElement.GetProperty("Version").GetInt64());
            Assert.Equal(0, view.RootElement.GetProperty("ConfigVersion").GetInt64());
            Assert.Equal(player.ToString(), view.RootElement.GetProperty("Me").GetProperty("Id").GetString());
            Assert.Equal(2, view.RootElement.GetProperty("Roster").GetProperty("Seats").GetArrayLength());
            Assert.True(view.RootElement.TryGetProperty("Body", out _));
            Assert.Equal(2, await ScalarAsync<int>(owner, "select revision from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session)));
            Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session)));
            Assert.Equal("b", await ScalarAsync<string>(owner, "select answer ->> 'option' from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session)));
            Assert.Equal(2L, await ScalarAsync<long>(owner, "select version from sessions where tenant_id = @tenant and id = @session", ("tenant", tenant), ("session", session)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reloaded_grain_replays_an_answer_correctly_whether_the_prior_commit_landed(bool priorCommitLanded)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertPlayersAsync(owner, tenant, 1);
        await InsertRunningSessionAsync(owner, tenant, session, player, 1);
        await InsertParticipantsAsync(owner, tenant, session, 1);
        await InsertItemsAsync(owner, tenant, session, 1);

        await using var sources = Sources();
        var command = new SessionCommand(player.ToString(), "answer", "0", 0, 0, 1, null, "{\"value\":\"saved\"}", DateTimeOffset.UtcNow);
        var firstGrain = CreateReplayGrain(sources, tenant, session,
            priorCommitLanded
                ? new CommitThenDisconnectDatabase(new TenantDb(sources))
                : new DisconnectBeforeCommitDatabase());
        var unknownOutcome = await firstGrain.ExecuteAsync(command);

        Assert.Equal("retryable", unknownOutcome.Code);
        Assert.True(unknownOutcome.Retryable);
        Assert.Null(GrainState(firstGrain));
        Assert.Equal(priorCommitLanded ? 1L : 0L,
            await ScalarAsync<long>(owner, "select version from sessions where tenant_id = @tenant and id = @session", ("tenant", tenant), ("session", session)));

        var reloadedGrain = CreateReplayGrain(sources, tenant, session);
        var replay = await reloadedGrain.ExecuteAsync(command);

        Assert.Equal("ok", replay.Code);
        Assert.NotNull(replay.ViewJson);
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select version from sessions where tenant_id = @tenant and id = @session", ("tenant", tenant), ("session", session)));
    }

    [SkippableFact]
    public async Task Renderer_failure_after_a_real_grain_commit_keeps_database_and_memory_advanced()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = PlayerId(1);
        await InsertTenantAsync(owner, tenant);
        await InsertPlayersAsync(owner, tenant, 1);
        await InsertRunningSessionAsync(owner, tenant, session, player, 1);
        await InsertParticipantsAsync(owner, tenant, session, 1);
        await InsertItemsAsync(owner, tenant, session, 1);

        await using var sources = Sources();
        var snapshot = Assert.IsType<SessionState>(await new SessionSnapshotLoader(new TenantDb(sources)).LoadAsync(tenant, session));
        var grain = new SessionGrain(
            new SessionSnapshotLoader(new TenantDb(sources)),
            new SessionStateStore(new TenantDb(sources)),
            new FormatRegistry([new PersistingFormat()]),
            new KindRegistry([new EchoKind()]),
            new TestClock(DateTimeOffset.UtcNow),
            new ThrowingViewRenderer(),
            new NoopDeadlines(),
            NullLogger<SessionGrain>.Instance);
        SetGrainState(grain, tenant, session, snapshot);

        var result = await grain.ExecuteAsync(new SessionCommand(player.ToString(), "answer", "0", 0, 0, 1, null, "{\"value\":\"saved\"}", DateTimeOffset.UtcNow));

        Assert.Equal("view_unavailable", result.Code);
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select version from sessions where tenant_id = @tenant and id = @session", ("tenant", tenant), ("session", session)));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from session_answers where tenant_id = @tenant and session_id = @session", ("tenant", tenant), ("session", session)));
        Assert.Equal(1L, GrainState(grain)!.Session.Version);
    }

    private static async Task<long> TrySaveAsync(SessionStateStore store, Guid tenant, Guid session, SessionState state)
    {
        try
        {
            return await store.SaveAsync(tenant, session, 0, state);
        }
        catch (StaleActivationException)
        {
            return -1;
        }
    }

    private static SessionState WithAnswer(SessionState state, Guid player, int revision, string answer)
    {
        using var document = JsonDocument.Parse($$"""{"value":"{{answer}}"}""");
        var answers = new Dictionary<AnswerKey, AnswerRecord>
        {
            [new(player.ToString(), 0)] = new(document.RootElement.Clone(), true, 1, 10, TimeSpan.FromMilliseconds(100), DateTimeOffset.UtcNow, Revision: revision)
        };
        return state with { Answers = SessionState.ReadOnly(answers) };
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from sessions; delete from tenants; delete from accounts");
        return owner;
    }

    private static Task InsertTenantAsync(NpgsqlConnection connection, Guid tenant) =>
        ExecuteAsync(connection, "insert into tenants (id, slug, name, default_language) values (@tenant, @slug, @slug, 'en')", ("tenant", tenant), ("slug", $"session-{tenant:N}"));

    private static Task InsertPlayersAsync(NpgsqlConnection connection, Guid tenant, int count) =>
        ExecuteAsync(connection, """
            insert into players (tenant_id, id, display_name, avatar_seed, lang)
            select @tenant, ('00000000-0000-0000-0000-' || lpad(value::text, 12, '0'))::uuid,
                   'Player', 'seed', 'en'
            from generate_series(1, @count) as value
            """, ("tenant", tenant), ("count", count));

    private static Task InsertRunningSessionAsync(NpgsqlConnection connection, Guid tenant, Guid session, Guid owner, int itemCount, string format = "trivia") =>
        ExecuteAsync(connection, """
            insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, slot, started_at)
            values (@tenant, @session, @code, @format, @owner, 'en', 'running', @capacity, '{}'::jsonb, '0', 0, now())
            """, ("tenant", tenant), ("session", session), ("code", session.ToString("N")), ("format", format), ("owner", owner), ("capacity", itemCount));

    private static Task InsertParticipantsAsync(NpgsqlConnection connection, Guid tenant, Guid session, int count) =>
        ExecuteAsync(connection, """
            insert into session_participants (tenant_id, session_id, player_id, seat, phase, slot)
            select @tenant, @session, ('00000000-0000-0000-0000-' || lpad(value::text, 12, '0'))::uuid,
                   value - 1, '0', 0
            from generate_series(1, @count) as value
            """, ("tenant", tenant), ("session", session), ("count", count));

    private static Task InsertItemsAsync(NpgsqlConnection connection, Guid tenant, Guid session, int count) =>
        ExecuteAsync(connection, """
            insert into session_items (tenant_id, session_id, slot, kind, kind_version, prompt, payload, resolved, card, reveal, level)
            select @tenant, @session, value - 1, 'trivia', 1, 'Question', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, 1
            from generate_series(1, @count) as value
            """, ("tenant", tenant), ("session", session), ("count", count));

    private static Task InsertPollChoiceItemAsync(NpgsqlConnection connection, Guid tenant, Guid session) =>
        ExecuteAsync(connection, """
            insert into session_items (tenant_id, session_id, slot, kind, kind_version, prompt, payload, resolved, card, reveal, level)
            values (@tenant, @session, 0, 'poll_choice', 1, 'Question',
                '{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}'::jsonb, '{}'::jsonb,
                '{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}'::jsonb, '{}'::jsonb, 1)
            """, ("tenant", tenant), ("session", session));

    private static Task InsertAnswersAsync(NpgsqlConnection connection, Guid tenant, Guid session, int count) =>
        ExecuteAsync(connection, """
            insert into session_answers (tenant_id, session_id, slot, player_id, revision, answer, credit, points, taken_ms, received_at)
            select @tenant, @session, 0, ('00000000-0000-0000-0000-' || lpad(value::text, 12, '0'))::uuid,
                   1, '{"value":"answer"}'::jsonb, 1, 10, 100, now()
            from generate_series(1, @count) as value
            """, ("tenant", tenant), ("session", session), ("count", count));

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static void SetGrainState(SessionGrain grain, Guid tenant, Guid session, SessionState state)
    {
        SetGrainIdentity(grain, tenant, session);
        typeof(SessionGrain).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(grain, state);
    }

    private static ReplaySessionGrain CreateReplayGrain(DatabaseSources sources, Guid tenant, Guid session, ITenantDb? storeDatabase = null)
    {
        var grain = new ReplaySessionGrain(
            new SessionSnapshotLoader(new TenantDb(sources)),
            new SessionStateStore(storeDatabase ?? new TenantDb(sources)),
            new FormatRegistry([new ReplayFormat()]),
            new KindRegistry([new EchoKind()]),
            new TestClock(DateTimeOffset.UtcNow),
            new CurrentViewRenderer(),
            new NoopDeadlines(),
            NullLogger<SessionGrain>.Instance);
        SetGrainIdentity(grain, tenant, session);
        return grain;
    }

    private static void SetGrainIdentity(SessionGrain grain, Guid tenant, Guid session)
    {
        var fields = typeof(SessionGrain);
        fields.GetField("_tenantId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(grain, tenant);
        fields.GetField("_sessionId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(grain, session);
    }

    private static SessionState? GrainState(SessionGrain grain) =>
        (SessionState?)typeof(SessionGrain).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(grain);

    private sealed class CountingTenantDatabase(ITenantDb inner) : ITenantDb
    {
        public int QueryCount { get; private set; }
        public System.Data.IsolationLevel? IsolationLevel { get; private set; }
        public bool? ReadOnly { get; private set; }

        public async Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false)
        {
            IsolationLevel = isolationLevel;
            ReadOnly = readOnly;
            return await inner.InTenantAsync(tenantId, transaction => callback(new CountingTenantTransaction(transaction, this)), isolationLevel, cancellationToken, readOnly);
        }

        public void Reset()
        {
            QueryCount = 0;
            IsolationLevel = null;
            ReadOnly = null;
        }

        private sealed class CountingTenantTransaction(ITenantTx inner, CountingTenantDatabase database) : ITenantTx
        {
            public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
            {
                database.QueryCount++;
                return inner.QueryAsync<T>(sql, parameters, cancellationToken);
            }

            public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) => inner.ExecuteAsync(sql, parameters, cancellationToken);

            public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
            {
                database.QueryCount++;
                return inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
            }
        }
    }

    private sealed class DisconnectBeforeCommitDatabase : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) =>
            Task.FromException<T>(new TenantCommitOutcomeUnknownException(new IOException("connection lost")));
    }

    private sealed class CommitThenDisconnectDatabase(ITenantDb inner) : ITenantDb
    {
        public async Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false)
        {
            _ = await inner.InTenantAsync(tenantId, callback, isolationLevel, cancellationToken, readOnly);
            throw new TenantCommitOutcomeUnknownException(new IOException("connection lost"));
        }
    }

    private sealed class PersistingFormat : IFormat
    {
        public string Id => "trivia";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 1, false, false, _ => true, false, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public void Apply(ISession session, Command command) => session.Record(session.Grade(command), 0);
        public object View(IViewing viewing) => new { };
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(0, []);
        public void Start(ISession session) { }
        public void Timeout(ISession session) { }
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }

    private sealed class ReplayFormat : IFormat
    {
        public string Id => "trivia";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 1, false, false, _ => true, false, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public void Apply(ISession session, Command command)
        {
            if (session.Answer(command.Player, command.At.Slot) is not null) return;
            session.Record(session.Grade(command), 0);
        }
        public object View(IViewing viewing) => new { };
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(0, []);
        public void Start(ISession session) { }
        public void Timeout(ISession session) { }
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }

    private sealed class ReplaySessionGrain(
        ISessionSnapshotLoader snapshots,
        SessionStateStore store,
        IFormatRegistry formats,
        IKindRegistry kinds,
        IClock clock,
        IViewRenderer views,
        ISessionDeadlineReconciler deadlines,
        Microsoft.Extensions.Logging.ILogger<SessionGrain> logger)
        : SessionGrain(snapshots, store, formats, kinds, clock, views, deadlines, logger)
    {
        protected override void Deactivate() { }
    }

    private sealed class EchoKind : IKindRuntime
    {
        public string Id => "trivia";
        public int Version => 1;
        public string Family => "test";
        public bool Gradable => true;
        public JsonElement Upgrade(int from, JsonElement payload) => payload;
        public JsonElement UpgradeSnapshot(int from, JsonElement snapshot) => snapshot;
        public IReadOnlyList<Problem> Validate(JsonElement payload, ValidateEnv env) => [];
        public JsonElement Resolve(JsonElement payload, ResolveContext context) => payload;
        public JsonElement Card(JsonElement payload, JsonElement resolved) => payload;
        public JsonElement Reveal(JsonElement payload, JsonElement resolved) => resolved;
        public JsonElement? Normalize(JsonElement payload, JsonElement resolved, JsonElement? answer) => answer?.Clone();
        public Grade Grade(JsonElement payload, JsonElement resolved, JsonElement? stored) => Quesshi.Shared.Grade.Of(1);
    }

    private sealed class ThrowingViewRenderer : IViewRenderer
    {
        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => throw new InvalidOperationException("renderer unavailable");
    }

    private sealed class CurrentViewRenderer : IViewRenderer
    {
        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => new(state.Session.Version, "{}");
    }

    private sealed class NoopDeadlines : ISessionDeadlineReconciler
    {
        public Task ReconcileAsync(SessionState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Guid PlayerId(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:D12}");

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task<string> XminAsync(NpgsqlConnection connection, string table, Guid tenant, Guid session, Guid player) =>
        ScalarAsync<string>(connection, $"select xmin::text from {table} where tenant_id = @tenant and session_id = @session and player_id = @player",
            ("tenant", tenant), ("session", session), ("player", player));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
