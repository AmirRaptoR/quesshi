using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orleans;
using Quesshi.Application.Sessions;
using Quesshi.Bench.Live;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure.Clock;
using Quesshi.Infrastructure.Database;
using Quesshi.Kinds;
using Quesshi.Kinds.Choice;
using Quesshi.Plugin.Api;
using Quesshi.Shared;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

/// <summary>
/// Batched live answers against PostgreSQL and real Orleans silos (architecture 7.4.1): durable acknowledgement,
/// exactly-once scoring, one close and one finish, and the format grace.
/// </summary>
public sealed class SessionBatchDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Question = BenchLiveFormat.Question;
    private static readonly TimeSpan Grace = BenchLiveFormat.Grace;

    [SkippableFact]
    public async Task Two_silos_keep_every_acknowledged_near_deadline_answer_durable_and_scored_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        const int players = 24;
        await using var owner = await PrepareAsync();
        var observer = new CountingObserver();
        await using var first = await StartSiloAsync(observer);
        await using var second = await StartSiloAsync(observer);
        var seeded = await SeedQuestionAsync(owner, players, untilDeadline: TimeSpan.FromSeconds(2));
        var silos = new[] { first, second };
        var deadline = seeded.Deadline;

        // Players 0-19 answer from 400 ms before the deadline to 600 ms after it (inside the grace),
        // alternating between the two silos. Players 0-3 submit the same answer twice at once.
        // Players 20-23 answer after the grace has ended.
        var sends = new List<Task<(int Player, SessionCommandResult Result)>>();
        for (var player = 0; player < players; player++)
        {
            var late = player >= 20;
            var at = late
                ? deadline + Grace + TimeSpan.FromMilliseconds(300 + player)
                : deadline + TimeSpan.FromMilliseconds(-400 + player * 50);
            var copies = player < 4 ? 2 : 1;
            for (var copy = 0; copy < copies; copy++)
            {
                var silo = silos[(player + copy) % 2];
                var index = player;
                sends.Add(SendAtAsync(silo, seeded, index, at));
            }
        }
        var results = await Task.WhenAll(sends);

        await WaitForPhaseAsync(owner, seeded, "2");
        var accepted = results.Where(result => result.Result.Code == "ok").Select(result => result.Player).Distinct().ToArray();
        Assert.Equal(20, accepted.Length);
        Assert.All(results.Where(result => result.Player >= 20), result => Assert.Contains(result.Result.Code, new[] { "late_answer", "stale_position" }));

        var rows = await AnswerRowsAsync(owner, seeded);
        Assert.Equal(players, rows.Count);
        foreach (var player in accepted)
            Assert.True(rows[seeded.Players[player]], $"Acknowledged player {player} has no durable answer.");
        Assert.Equal(accepted.Length, rows.Count(row => row.Value));
        Assert.Equal(players - accepted.Length, rows.Count(row => !row.Value));
        Assert.Equal(accepted.Length, await ScalarAsync<long>(owner, "select coalesce(sum(score), 0) from session_participants where tenant_id = @tenant and session_id = @session", ("tenant", seeded.Tenant), ("session", seeded.Session)));
        Assert.Equal(1, observer.Count(SessionTurnKind.RoundClose));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lost_batch_commit_is_retried_without_losing_or_double_scoring_an_answer(bool commitLanded)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        const int players = 8;
        await using var owner = await PrepareAsync();
        var database = new FaultingTenantDb();
        await using var silo = await StartSiloAsync(new CountingObserver(), database);
        var seeded = await SeedQuestionAsync(owner, players, untilDeadline: TimeSpan.FromSeconds(30));
        database.FailNextWrite(commitLanded);

        // Every player retries while told the outcome is unknown. A retry that finds the round already closed (the
        // other players' answers completed it) is told the position is stale, which is also a settled outcome.
        var finals = await Task.WhenAll(Enumerable.Range(0, players).Select(async player =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var result = await SendAsync(silo, seeded, player, DateTimeOffset.UtcNow);
                if (!result.Retryable) return (Player: player, Attempts: attempt + 1, result.Code);
            }
            throw new XunitException($"Player {player} never got a settled answer.");
        }));

        Assert.All(finals, final => Assert.Contains(final.Code, new[] { "ok", "stale_position" }));
        Assert.True(finals.Any(final => final.Attempts > 1), "The injected fault was never observed by a caller.");
        var rows = await AnswerRowsAsync(owner, seeded);
        Assert.Equal(players, rows.Count);
        Assert.All(rows.Values, answered => Assert.True(answered));
        Assert.Equal(players, await ScalarAsync<long>(owner, "select coalesce(sum(score), 0) from session_participants where tenant_id = @tenant and session_id = @session", ("tenant", seeded.Tenant), ("session", seeded.Session)));
        Assert.Equal(players, await ScalarAsync<long>(owner, "select count(*) from session_answers where tenant_id = @tenant and session_id = @session and answer is not null", ("tenant", seeded.Tenant), ("session", seeded.Session)));
    }

    [SkippableFact]
    public async Task Last_answer_and_timeout_racing_a_batch_commit_close_the_round_once_and_finish_once()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        const int players = 4;
        await using var owner = await PrepareAsync();
        var database = new FaultingTenantDb();
        var observer = new CountingObserver();
        var events = new CountingEvents();
        await using var silo = await StartSiloAsync(observer, database, events);
        var seeded = await SeedQuestionAsync(owner, players, untilDeadline: TimeSpan.FromSeconds(1.5));

        // Hold the first batch's commit while two more answers are accepted, the deadline's timeout fires,
        // and the last answer arrives. Everything queues behind the batch that is being written.
        database.HoldNextWrite();
        var firstAnswer = SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow);
        await database.WriteHeld.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = new[] { SendAsync(silo, seeded, 1, DateTimeOffset.UtcNow), SendAsync(silo, seeded, 2, DateTimeOffset.UtcNow) };
        await Task.Delay(seeded.Deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(400));
        var last = SendAsync(silo, seeded, 3, DateTimeOffset.UtcNow);
        Assert.False(firstAnswer.IsCompleted, "An answer was acknowledged before its batch committed.");
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        database.ReleaseHeldWrite();

        Assert.All(await Task.WhenAll(pending.Append(firstAnswer).Append(last)), result => Assert.Equal("ok", result.Code));
        await WaitForStatusAsync(owner, seeded, "finished");

        var rows = await AnswerRowsAsync(owner, seeded);
        Assert.Equal(players, rows.Count);
        Assert.All(rows.Values, answered => Assert.True(answered));
        Assert.Equal(players, await ScalarAsync<long>(owner, "select coalesce(sum(score), 0) from session_participants where tenant_id = @tenant and session_id = @session", ("tenant", seeded.Tenant), ("session", seeded.Session)));
        Assert.Equal(1, observer.Count(SessionTurnKind.RoundClose));
        Assert.Equal(1, events.Ended(seeded.Session));
        Assert.Equal(players, await ScalarAsync<long>(owner, "select count(*) from session_participants where tenant_id = @tenant and session_id = @session and place is not null", ("tenant", seeded.Tenant), ("session", seeded.Session)));
    }

    [SkippableFact]
    public async Task A_timeout_command_waiting_for_a_held_batch_commits_answers_before_recording_only_unanswered_misses()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        const int players = 5;
        await using var owner = await PrepareAsync();
        var database = new FaultingTenantDb();
        var observer = new CountingObserver();
        var events = new CountingEvents();
        await using var silo = await StartSiloAsync(observer, database, events);
        var seeded = await SeedQuestionAsync(owner, players, untilDeadline: TimeSpan.FromSeconds(1));

        // Players 0-2 answer while a batch commit is held; player 3 answers after the grace; player 4 never answers.
        database.HoldNextWrite();
        var held = SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow);
        await database.WriteHeld.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = new[] { SendAsync(silo, seeded, 1, DateTimeOffset.UtcNow), SendAsync(silo, seeded, 2, DateTimeOffset.UtcNow) };
        await Task.Delay(seeded.Deadline + Grace + TimeSpan.FromMilliseconds(500) - DateTimeOffset.UtcNow);
        var afterGrace = SendAsync(silo, seeded, 3, DateTimeOffset.UtcNow);
        await Task.Delay(300);
        Assert.False(afterGrace.IsCompleted, "The timeout command completed while the older answer batch was still uncommitted.");
        database.ReleaseHeldWrite();

        Assert.All(await Task.WhenAll(waiting.Append(held)), result => Assert.Equal("ok", result.Code));
        Assert.Contains((await afterGrace).Code, new[] { "late_answer", "stale_position" });
        await WaitForStatusAsync(owner, seeded, "finished");

        var rows = await AnswerRowsAsync(owner, seeded);
        Assert.Equal(players, rows.Count);
        Assert.Equal([true, true, true, false, false], seeded.Players.Select(player => rows[player]));
        Assert.Equal(3, await ScalarAsync<long>(owner, "select coalesce(sum(score), 0) from session_participants where tenant_id = @tenant and session_id = @session", ("tenant", seeded.Tenant), ("session", seeded.Session)));
        Assert.Equal(1, observer.Count(SessionTurnKind.RoundClose));
        Assert.Equal(1, events.Ended(seeded.Session));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_of_an_answer_in_a_batch_being_written_waits_for_that_batch_and_is_not_told_ok_if_it_is_lost(bool commits)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var database = new FaultingTenantDb();
        await using var silo = await StartSiloAsync(new CountingObserver(), database);
        var seeded = await SeedQuestionAsync(owner, 3, untilDeadline: TimeSpan.FromSeconds(30));

        database.HoldNextWrite();
        if (!commits) database.FailNextWrite(commitLanded: false);
        var original = SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow);
        await database.WriteHeld.WaitAsync(TimeSpan.FromSeconds(10));
        var repeat = SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow);
        await Task.Delay(300);
        Assert.False(repeat.IsCompleted, "A repeat was answered while the batch holding its original was uncommitted.");
        database.ReleaseHeldWrite();

        var repeated = await repeat;
        var first = await original;
        if (commits)
        {
            Assert.Equal("ok", repeated.Code);
            Assert.Equal("ok", first.Code);
            Assert.True((await AnswerRowsAsync(owner, seeded))[seeded.Players[0]]);
        }
        else
        {
            Assert.NotEqual("ok", repeated.Code);
            Assert.True(repeated.Retryable);
            Assert.True(first.Retryable);
            Assert.DoesNotContain(true, (await AnswerRowsAsync(owner, seeded)).Values);
        }
    }

    [SkippableFact]
    public async Task Rejected_commands_during_a_held_batch_show_only_committed_state()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var database = new FaultingTenantDb();
        await using var silo = await StartSiloAsync(new CountingObserver(), database);
        var seeded = await SeedQuestionAsync(owner, 3, untilDeadline: TimeSpan.FromSeconds(30));
        var before = await SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow, slot: 5);
        Assert.Equal("stale_position", before.Code);

        database.HoldNextWrite();
        var held = SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow);
        await database.WriteHeld.WaitAsync(TimeSpan.FromSeconds(10));
        var during = await SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow, slot: 5);
        Assert.Equal("stale_position", during.Code);
        Assert.Equal(before.Version, during.Version);
        Assert.Equal(WithoutTimes(before.ViewJson), WithoutTimes(during.ViewJson));
        Assert.False(held.IsCompleted);
        database.ReleaseHeldWrite();
        Assert.Equal("ok", (await held).Code);

        var after = await SendAsync(silo, seeded, 0, DateTimeOffset.UtcNow, slot: 5);
        Assert.True(after.Version > before.Version);
        Assert.NotEqual(WithoutTimes(during.ViewJson), WithoutTimes(after.ViewJson));
    }

    private static string? WithoutTimes(string? json) =>
        json is null ? null : System.Text.RegularExpressions.Regex.Replace(json, @"\d{4}-\d\d-\d\dT[0-9:.]+(\\u002B|\+|Z)[0-9:]*", "<time>");

    private Task<(int Player, SessionCommandResult Result)> SendAtAsync(TestSilo silo, Seeded seeded, int player, DateTimeOffset at) =>
        Task.Run(async () =>
        {
            var delay = at - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            return (player, await SendAsync(silo, seeded, player, DateTimeOffset.UtcNow));
        });

    private static Task<SessionCommandResult> SendAsync(TestSilo silo, Seeded seeded, int player, DateTimeOffset receivedAt, int slot = 0) =>
        silo.Grains.GetGrain<ISessionGrain>($"{seeded.Tenant}/{seeded.Session}").ExecuteAsync(
            new SessionCommand(seeded.Players[player].ToString(), "answer", "1", slot, 0, 0, null, "{\"index\":0}", receivedAt));

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        await ExecuteAsync(owner, "delete from sessions; delete from tenants; delete from accounts");
        return owner;
    }

    private async Task<Seeded> SeedQuestionAsync(NpgsqlConnection owner, int count, TimeSpan untilDeadline)
    {
        var ownerConnection = fixture.ConnectionStringForRole("quesshi_owner");
        var tenant = await BenchmarkFixture.SeedTenantAsync(ownerConnection, $"batch-{Guid.NewGuid():N}.test");
        var ids = Enumerable.Range(1, count).Select(number => Guid.Parse($"00000000-0000-0000-0000-{number:D12}")).ToArray();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) select @tenant, id, 'Player', 'seed', 'en' from unnest(@ids) as id", ("tenant", tenant.Id), ("ids", ids));
        var session = await BenchmarkFixture.SeedSessionAsync(ownerConnection, tenant, new BenchmarkOptions(count, LiveScenario.Burst, null), ids);
        var deadline = DateTimeOffset.UtcNow + untilDeadline;
        await ExecuteAsync(owner, """
            update sessions set phase = '1', slot = 0, step = 0, phase_started_at = @opened, due_at = @deadline
            where tenant_id = @tenant and id = @session
            """, ("tenant", tenant.Id), ("session", session.Id), ("opened", deadline - Question), ("deadline", deadline));
        return new Seeded(tenant.Id, session.Id, ids, deadline);
    }

    private async Task<TestSilo> StartSiloAsync(ISessionTurnObserver observer, ITenantDb? wrapper = null, IEvents? events = null)
    {
        var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
        var app = Program.CreateApplication(
            sources,
            TestPorts.Allocate(),
            TestPorts.Allocate(),
            configuredFormats: new FormatRegistry([new BenchLiveFormat()]),
            configuredKinds: new KindRegistry([new ChoiceKind()]),
            turnObserver: observer,
            configureServices: services =>
            {
                if (wrapper is FaultingTenantDb faulting)
                    services.Replace(ServiceDescriptor.Singleton<ITenantDb>(provider => faulting.Wrap(new TenantDb(provider.GetRequiredService<DatabaseSources>()))));
                if (events is not null)
                    services.Replace(ServiceDescriptor.Singleton<IEvents>(events));
            });
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        await app.Services.GetRequiredService<PostgresClock>().StartAsync();
        return new TestSilo(app);
    }

    private static async Task WaitForPhaseAsync(NpgsqlConnection owner, Seeded seeded, string phase) =>
        await WaitAsync(owner, "select phase = @expected from sessions where tenant_id = @tenant and id = @session", seeded, phase);

    private static async Task WaitForStatusAsync(NpgsqlConnection owner, Seeded seeded, string status) =>
        await WaitAsync(owner, "select status = @expected from sessions where tenant_id = @tenant and id = @session", seeded, status);

    private static async Task WaitAsync(NpgsqlConnection owner, string sql, Seeded seeded, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await ScalarAsync<bool>(owner, sql, ("tenant", seeded.Tenant), ("session", seeded.Session), ("expected", expected)))
        {
            if (timeout.IsCancellationRequested) throw new XunitException($"The session never reached '{expected}'.");
            await Task.Delay(50);
        }
    }

    /// <summary>One entry per player with an answer row; true when the row holds an answer rather than a miss.</summary>
    private static async Task<Dictionary<Guid, bool>> AnswerRowsAsync(NpgsqlConnection owner, Seeded seeded)
    {
        await using var command = new NpgsqlCommand("select player_id, answer is not null from session_answers where tenant_id = @tenant and session_id = @session", owner);
        command.Parameters.AddWithValue("tenant", seeded.Tenant);
        command.Parameters.AddWithValue("session", seeded.Session);
        var rows = new Dictionary<Guid, bool>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetGuid(0), reader.GetBoolean(1));
        return rows;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Seeded(Guid Tenant, Guid Session, Guid[] Players, DateTimeOffset Deadline);

    private sealed class TestSilo(Microsoft.AspNetCore.Builder.WebApplication app) : IAsyncDisposable
    {
        public IGrainFactory Grains => app.Services.GetRequiredService<IGrainFactory>();

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class CountingObserver : ISessionTurnObserver
    {
        private readonly ConcurrentQueue<SessionTurnKind> _kinds = [];
        public void Record(SessionTurnSample sample) => _kinds.Enqueue(sample.Kind);
        public void RecordTimerLag(TimeSpan lag) { }
        public int Count(SessionTurnKind kind) => _kinds.Count(candidate => candidate == kind);
    }

    private sealed class CountingEvents : IEvents
    {
        private readonly ConcurrentQueue<Guid> _ended = [];
        public int Ended(Guid session) => _ended.Count(candidate => candidate == session);

        public Task EmitAsync<TEvent>(ITenantTx transaction, TEvent @event, CancellationToken cancellationToken = default)
        {
            if (@event is SessionEnded ended) _ended.Enqueue(ended.SessionId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Delays or breaks the next tenant write so a test can force the order in which commands meet a batch.</summary>
    private sealed class FaultingTenantDb : ITenantDb
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITenantDb _inner = null!;
        private int _holdNext;
        private int _failNext;
        private bool _landed;

        public Task WriteHeld => _held.Task;

        public FaultingTenantDb Wrap(ITenantDb inner)
        {
            _inner = inner;
            return this;
        }

        public void HoldNextWrite() => Volatile.Write(ref _holdNext, 1);
        public void ReleaseHeldWrite() => _release.TrySetResult();

        public void FailNextWrite(bool commitLanded)
        {
            _landed = commitLanded;
            Volatile.Write(ref _failNext, 1);
        }

        public async Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false)
        {
            if (!readOnly && Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                _held.TrySetResult();
                await _release.Task;
            }
            if (!readOnly && Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                if (_landed) _ = await _inner.InTenantAsync(tenantId, callback, isolationLevel, cancellationToken, readOnly);
                throw new TenantCommitOutcomeUnknownException(new IOException("connection lost"));
            }
            return await _inner.InTenantAsync(tenantId, callback, isolationLevel, cancellationToken, readOnly);
        }
    }
}
