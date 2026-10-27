using System.Reflection;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Poll;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;
using Quesshi.Kinds.PollChoice;
using Quesshi.Plugin.Api;
using Quesshi.Server.Sessions;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

public sealed class SessionGrainBehaviorTests
{
    private const string Player = "00000000-0000-0000-0000-000000000001";
    [Fact]
    public async Task Terminal_and_backoff_commands_do_not_reach_the_format_or_store()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var format = new RecordingFormat();
        var deadlines = new RecordingDeadlines();
        var terminal = CreateGrain(State(ended: true), format, deadlines, now);

        var terminalResult = await terminal.ExecuteAsync(Command(now));

        Assert.Equal("terminal", terminalResult.Code);
        Assert.Equal(0, deadlines.Count);
        Assert.Equal(0, format.ApplyCount);

        var backoff = CreateGrain(State(retryAt: now.AddMinutes(1)), format, deadlines, now);
        var busyResult = await backoff.ExecuteAsync(Command(now));

        Assert.Equal("session_busy", busyResult.Code);
        Assert.Equal(1, deadlines.Count);
        Assert.Equal(0, format.ApplyCount);
    }

    [Fact]
    public async Task Cancelled_sessions_render_without_invoking_the_format()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var state = State(ended: true);
        state = state with { Session = state.Session with { Status = "cancelled" } };
        var renderer = new CancelledRenderer();
        var grain = CreateGrain(state, new RecordingFormat(), new RecordingDeadlines(), now, renderer: renderer, formatRegistry: new FormatRegistry([]));

        var result = await grain.ExecuteAsync(Command(now));

        Assert.Equal("terminal", result.Code);
        Assert.Equal(1, renderer.CancelledCount);
    }

    [Fact]
    public async Task Format_rejection_and_late_handling_return_current_view_without_apply()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
        var rejectedFormat = new RecordingFormat { Rejection = new SessionCommandException("invalid_command") };
        var rejected = CreateGrain(State(), rejectedFormat, new RecordingDeadlines(), now);

        var invalid = await rejected.ExecuteAsync(Command(now, type: "next"));

        Assert.Equal("invalid_command", invalid.Code);
        Assert.Equal(1, rejectedFormat.ApplyCount);
        Assert.NotNull(invalid.ViewJson);

        var lateFormat = new RecordingFormat();
        var late = CreateGrain(State(dueAt: now.AddMilliseconds(-1)), lateFormat, new RecordingDeadlines(), now);

        var result = await late.ExecuteAsync(Command(now.AddMilliseconds(-2), type: "answer"));

        Assert.Equal("late_answer", result.Code);
        Assert.Equal(0, lateFormat.ApplyCount);
        Assert.NotNull(result.ViewJson);
    }

    [Fact]
    public async Task Definite_database_failure_keeps_the_current_state_but_a_render_failure_keeps_a_committed_state()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var direct = State();
        Assert.Equal(1, await new SessionStateStore(new SavingDatabase()).SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, direct));
        var original = State();
        var rolledBack = CreateGrain(original, new MutatingFormat(), new RecordingDeadlines(), now, database: new FailingDatabase(new KnownDatabaseException()));

        var failure = await rolledBack.ExecuteAsync(Command(now));

        Assert.Equal("write_failed", failure.Code);
        Assert.Same(original, StateOf(rolledBack));
        Assert.Equal("{}", original.FormatState.GetRawText());
        Assert.Equal(0, rolledBack.DeactivateCount);

        var committed = CreateGrain(State(), new MutatingFormat(), new RecordingDeadlines(), now, database: new SavingDatabase(), renderer: new ThrowingRenderer());
        var unavailable = await committed.ExecuteAsync(Command(now));

        Assert.Equal("view_unavailable", unavailable.Code);
        Assert.Equal(1, StateOf(committed)!.Session.Version);
    }

    [Fact]
    public async Task Post_commit_snapshot_reload_failures_discard_the_stale_cached_state()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var grain = CreateGrain(new ThrowingSnapshotLoader(), new SlotMovingFormat(), new RecordingDeadlines(), now,
            database: new SavingDatabase(), initialState: State());

        // A command that is not a deferred answer commits at once; moving to another slot reloads the durable snapshot.
        var result = await grain.ExecuteAsync(Command(now, type: "next"));

        Assert.Equal("view_unavailable", result.Code);
        Assert.True(result.Retryable);
        Assert.Null(StateOf(grain));
        Assert.Equal(1, grain.DeactivateCount);
    }

    [Fact]
    public async Task Stale_commit_outcomes_discard_the_cached_snapshot_before_retry()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var loader = new ToggleSnapshotLoader { State = State() };
        var stale = CreateGrain(loader, new MutatingFormat(), new RecordingDeadlines(), now, database: new FailingDatabase(new StaleActivationException()));

        var staleResult = await stale.ExecuteAsync(Command(now));

        Assert.Equal("stale_activation", staleResult.Code);
        Assert.True(staleResult.Retryable);
        Assert.Equal(1, stale.DeactivateCount);

        loader.State = State(ended: true);
        var retry = await stale.ExecuteAsync(Command(now));

        Assert.Equal("terminal", retry.Code);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task Repeated_unknown_commit_outcomes_request_deactivation_once()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var loader = new ToggleSnapshotLoader { State = State() };
        var unknown = CreateGrain(loader, new MutatingFormat(), new RecordingDeadlines(), now,
            database: new FailingDatabase(new TenantCommitOutcomeUnknownException(new IOException("connection lost"))));

        var unknownResult = await unknown.ExecuteAsync(Command(now));
        var retry = await unknown.ExecuteAsync(Command(now));

        Assert.Equal("retryable", unknownResult.Code);
        Assert.True(unknownResult.Retryable);
        Assert.Equal("retryable", retry.Code);
        Assert.True(retry.Retryable);
        Assert.Equal(2, loader.LoadCount);
        Assert.Equal(1, unknown.DeactivateCount);
    }

    [Fact]
    public async Task An_answer_queued_behind_a_failing_batch_is_still_written()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var database = new HeldFailingDatabase(new TenantCommitOutcomeUnknownException(new IOException("connection lost")));
        var deadlines = new HoldingDeadlines();
        var grain = CreateGrain(new ToggleSnapshotLoader { State = State() }, new MutatingFormat(), deadlines, now, database: database);

        var first = grain.ExecuteAsync(Command(now));
        await database.WriteHeld;
        deadlines.HoldNext();
        var second = grain.ExecuteAsync(Command(now));
        await deadlines.Held;
        // The failed batch queues for the gate that the second answer holds, and the third answer queues behind it,
        // so the third is accepted after the batch failed but before its flush loop has finished.
        database.FailHeldWrite();
        await Task.Delay(100);
        var third = grain.ExecuteAsync(Command(now));
        deadlines.Release();

        Assert.Equal("retryable", (await first.WaitAsync(TimeSpan.FromSeconds(30))).Code);
        Assert.Equal("retryable", (await second.WaitAsync(TimeSpan.FromSeconds(30))).Code);
        Assert.Equal("retryable", (await third.WaitAsync(TimeSpan.FromSeconds(30))).Code);
        Assert.Equal(2, database.WriteCount);
    }

    [Fact]
    public async Task Missing_sessions_are_not_cached_and_can_be_loaded_after_insertion()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var loader = new ToggleSnapshotLoader();
        var grain = CreateGrain(loader, new RecordingFormat(), new RecordingDeadlines(), now);

        Assert.Equal("not_found", (await grain.ExecuteAsync(Command(now))).Code);
        Assert.Null(StateOf(grain));
        Assert.Equal(1, grain.DeactivateCount);

        loader.State = State();
        var found = await grain.ExecuteAsync(Command(now));

        Assert.Equal("ok", found.Code);
        Assert.NotNull(StateOf(grain));
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task Stale_positions_and_repeated_personal_nexts_do_not_write_again()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var sharedStore = new SavingDatabase();
        var stale = CreateGrain(State(), new MutatingFormat(), new RecordingDeadlines(), now, database: sharedStore);

        var staleResult = await stale.ExecuteAsync(Command(now, slot: 1));

        Assert.Equal("stale_position", staleResult.Code);
        Assert.Equal(0, sharedStore.CallCount);

        var personalStore = new SavingDatabase();
        var personal = CreateGrain(State(), new AdvancingFormat(), new RecordingDeadlines(), now, database: personalStore);
        var first = await personal.ExecuteAsync(Command(now, type: "next"));
        var repeated = await personal.ExecuteAsync(Command(now, type: "next"));

        Assert.Equal("ok", first.Code);
        Assert.Equal("stale_position", repeated.Code);
        Assert.Equal(1, personalStore.CallCount);
    }

    [Fact]
    public async Task Answer_grading_honours_grace_boundaries_and_caps_taken_time()
    {
        var opened = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var due = opened.AddSeconds(10);
        var beforeGrace = due.AddMilliseconds(4);
        var saving = new SavingDatabase();
        var accepted = CreateGrain(State(dueAt: due, startedAt: opened), new AnsweringFormat(TimeSpan.FromMilliseconds(5)), new RecordingDeadlines(), beforeGrace,
            database: saving, kinds: new KindRegistry([new EchoKind()]));

        var result = await accepted.ExecuteAsync(Command(beforeGrace, payload: "{\"value\":\"a\"}"));

        Assert.Equal("ok", result.Code);
        var answer = Assert.Single(StateOf(accepted)!.Answers).Value;
        Assert.Equal(TimeSpan.FromSeconds(10), answer.Taken);
        Assert.Equal(1, saving.CallCount);

        var duplicate = await accepted.ExecuteAsync(Command(beforeGrace, payload: "{\"value\":\"a\"}"));

        Assert.Equal("invalid_command", duplicate.Code);
        Assert.Equal(1, saving.CallCount);

        var lateReceipt = due.AddMilliseconds(6);
        var late = CreateGrain(State(dueAt: due, startedAt: opened), new AnsweringFormat(TimeSpan.FromMilliseconds(5)), new RecordingDeadlines(), lateReceipt,
            database: new SavingDatabase(), kinds: new KindRegistry([new EchoKind()]));
        var lateResult = await late.ExecuteAsync(Command(lateReceipt, payload: "{\"value\":\"a\"}"));

        Assert.Equal("late_answer", lateResult.Code);
        Assert.Empty(StateOf(late)!.Answers);

        var lateHandling = CreateGrain(State(dueAt: due, startedAt: opened), new AnsweringFormat(TimeSpan.FromMilliseconds(5)), new RecordingDeadlines(), lateReceipt,
            database: new SavingDatabase(), kinds: new KindRegistry([new EchoKind()]));
        var lateHandlingResult = await lateHandling.ExecuteAsync(Command(beforeGrace, payload: "{\"value\":\"a\"}"));

        Assert.Equal("late_answer", lateHandlingResult.Code);
    }

    [Fact]
    public async Task Revisions_replace_in_place_and_retries_or_conflicts_do_not_write()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var saving = new SavingDatabase();
        var grain = CreateGrain(State(), new RevisionFormat(), new RecordingDeadlines(), now, database: saving,
            kinds: new KindRegistry([new EchoKind()]));

        Assert.Equal("ok", (await grain.ExecuteAsync(Command(now, payload: "{\"value\":\"first\"}", revision: 1))).Code);
        Assert.Equal("ok", (await grain.ExecuteAsync(Command(now, payload: "{\"value\":\"first\"}", revision: 1))).Code);
        Assert.Equal("revision_conflict", (await grain.ExecuteAsync(Command(now, payload: "{\"value\":\"different\"}", revision: 1))).Code);
        Assert.Equal("ok", (await grain.ExecuteAsync(Command(now, payload: "{\"value\":\"ignored\"}", revision: 0))).Code);
        Assert.Equal("ok", (await grain.ExecuteAsync(Command(now, payload: "{\"value\":\"second\"}", revision: 2))).Code);

        var answer = Assert.Single(StateOf(grain)!.Answers).Value;
        Assert.Equal(2, answer.Revision);
        Assert.Equal("second", answer.Stored?.GetProperty("value").GetString());
        Assert.Equal(2, saving.CallCount);
    }

    [Fact]
    public async Task Conflicting_pending_poll_revision_is_rejected_without_acknowledging_the_original_answer()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var database = new SavingDatabase();
        database.HoldNextWrite();
        var snapshot = PollState();
        var loader = new CurrentSnapshotLoader { Current = () => snapshot };
        var grain = CreateGrain(loader, new PollFormat(), new RecordingDeadlines(), now, database: database,
            kinds: new KindRegistry([new PollChoiceKind()]));

        var original = grain.ExecuteAsync(Command(now, payload: "{\"option\":\"yes\"}", revision: 1));
        await database.WriteHeld.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var conflict = grain.ExecuteAsync(Command(now, payload: "{\"option\":\"no\"}", revision: 1));

            Assert.Same(conflict, await Task.WhenAny(conflict, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.Equal("revision_conflict", (await conflict).Code);
            Assert.False(original.IsCompleted);
        }
        finally
        {
            database.ReleaseHeldWrite();
        }

        Assert.Equal("ok", (await original).Code);
    }

    [Fact]
    public async Task Exact_duplicate_pending_poll_revision_waits_for_the_original_batch()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var database = new SavingDatabase();
        database.HoldNextWrite();
        var snapshot = PollState();
        var loader = new CurrentSnapshotLoader { Current = () => snapshot };
        var grain = CreateGrain(loader, new PollFormat(), new RecordingDeadlines(), now, database: database,
            kinds: new KindRegistry([new PollChoiceKind()]));

        var original = grain.ExecuteAsync(Command(now, payload: "{\"option\":\"yes\"}", revision: 1));
        await database.WriteHeld.WaitAsync(TimeSpan.FromSeconds(1));
        var duplicate = grain.ExecuteAsync(Command(now, payload: "{\"option\":\"yes\"}", revision: 1));

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(duplicate.IsCompleted);

        database.ReleaseHeldWrite();

        Assert.Equal("ok", (await original).Code);
        Assert.Equal("ok", (await duplicate).Code);
    }

    [Fact]
    public async Task Timer_and_sweep_deliveries_of_one_due_identity_persist_one_timeout()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
        var started = now.AddHours(-1);
        var due = now.AddSeconds(-1);
        var format = new TimeoutFormat();
        var store = new SavingDatabase();
        var loader = new CurrentSnapshotLoader();
        var grain = CreateGrain(loader, format, new RecordingDeadlines(), now, database: store,
            initialState: State(dueAt: due, startedAt: started));
        loader.Current = () => StateOf(grain)! with
        {
            Session = StateOf(grain)!.Session with { DueAt = store.SavedDueAt }
        };

        // The timer and sweep share this delivery method, so repeat the same eligible due identity.
        await grain.TimeoutAsync(due);
        await grain.TimeoutAsync(due);
        await grain.TimeoutAsync(due);

        Assert.Equal(1, format.TimeoutCount);
        Assert.Equal(1, store.CallCount);
        Assert.Equal(started.AddDays(90), StateOf(grain)!.Session.DueAt);

        var backoffFormat = new TimeoutFormat();
        var backingOff = CreateGrain(State(dueAt: now.AddSeconds(-1), retryAt: now.AddSeconds(1)), backoffFormat, new RecordingDeadlines(), now);
        await backingOff.TimeoutAsync(now.AddSeconds(-1));

        Assert.Equal(0, backoffFormat.TimeoutCount);
    }

    [Fact]
    public async Task Timeout_persistence_uncertainty_discards_the_cached_snapshot()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
        var due = now.AddSeconds(-1);
        var stale = CreateGrain(State(dueAt: due), new TimeoutFormat(), new RecordingDeadlines(), now,
            database: new FailingDatabase(new StaleActivationException()));

        await Assert.ThrowsAsync<StaleActivationException>(() => stale.TimeoutAsync(due));
        Assert.Null(StateOf(stale));
        Assert.Equal(1, stale.DeactivateCount);

        var unknown = CreateGrain(State(dueAt: due), new TimeoutFormat(), new RecordingDeadlines(), now,
            database: new FailingDatabase(new TenantCommitOutcomeUnknownException(new IOException("connection lost"))));

        await Assert.ThrowsAsync<TenantCommitOutcomeUnknownException>(() => unknown.TimeoutAsync(due));
        Assert.Null(StateOf(unknown));
        Assert.Equal(1, unknown.DeactivateCount);

        var reload = CreateGrain(new ThrowingSnapshotLoader(), new SlotMovingFormat(), new RecordingDeadlines(), now,
            database: new SavingDatabase(), initialState: State(dueAt: due));

        await Assert.ThrowsAsync<KnownDatabaseException>(() => reload.TimeoutAsync(due));
        Assert.Null(StateOf(reload));
        Assert.Equal(1, reload.DeactivateCount);
    }

    [Fact]
    public async Task Expired_and_excessively_late_deadlines_end_without_invoking_the_format()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:01:00Z");
        var expiredStore = new SavingDatabase();
        var expired = CreateGrain(State(dueAt: now.AddSeconds(-1), startedAt: now.AddDays(-91)), new ThrowingTimeoutFormat(), new RecordingDeadlines(), now, database: expiredStore);

        await expired.TimeoutAsync(now.AddSeconds(-1));

        Assert.Equal(1, expiredStore.CallCount);

        var outageStore = new SavingDatabase();
        var outage = CreateGrain(State(dueAt: now.AddSeconds(-41)), new LatenessFormat(TimeSpan.FromSeconds(40)), new RecordingDeadlines(), now, database: outageStore);

        await outage.TimeoutAsync(now.AddSeconds(-41));

        Assert.Equal(1, outageStore.CallCount);

        var timelyFormat = new LatenessFormat(TimeSpan.FromSeconds(40));
        var timely = CreateGrain(State(dueAt: now.AddSeconds(-39)), timelyFormat, new RecordingDeadlines(), now, database: new SavingDatabase());

        await timely.TimeoutAsync(now.AddSeconds(-39));

        Assert.Equal(1, timelyFormat.TimeoutCount);
    }

    [Fact]
    public async Task Unexpected_format_failures_are_recorded_as_retryable_faults()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var database = new SavingDatabase();
        var grain = CreateGrain(State(), new ThrowingFormat(), new RecordingDeadlines(), now, database: database);

        var result = await grain.ExecuteAsync(Command(now));

        Assert.Equal("session_busy", result.Code);
        Assert.True(result.Retryable);
        Assert.Equal(1, database.CallCount);
    }

    [Fact]
    public async Task An_idle_48_hour_deadline_is_woken_and_applied_by_the_sweep()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var due = now.AddHours(48);
        var clock = new TestClock(now);
        var format = new TimeoutFormat();
        var store = new SavingDatabase();
        var loader = new CurrentSnapshotLoader();
        var grain = CreateGrain(loader, format, new RecordingDeadlines(), now, database: store,
            initialState: State(dueAt: due, startedAt: now), clock: clock);
        loader.Current = () => StateOf(grain)! with
        {
            Session = StateOf(grain)!.Session with { DueAt = store.SavedDueAt }
        };

        await grain.ExecuteAsync(Command(now));

        Assert.Null(DeadlineTimerOf(grain));

        clock.Advance(TimeSpan.FromHours(48).Add(TimeSpan.FromSeconds(1)));
        var dispatched = new List<Guid>();
        var session = new DueSession(Guid.NewGuid(), Guid.NewGuid(), due);
        var sweep = new DeadlineSweep(new SweepSource([session]), new SweepDispatcher(item =>
        {
            dispatched.Add(item.SessionId);
            return grain.TimeoutAsync(item.DueAt);
        }), clock, NullLogger<DeadlineSweep>.Instance);

        await sweep.RunPassAsync();

        Assert.Equal([session.SessionId], dispatched);
        Assert.Equal(1, format.TimeoutCount);
        Assert.Equal(1, store.CallCount);
    }

    private static TestSessionGrain CreateGrain(SessionState state, IFormat format, ISessionDeadlineReconciler deadlines, DateTimeOffset now, ITenantDb? database = null, IViewRenderer? renderer = null, IKindRegistry? kinds = null, IFormatRegistry? formatRegistry = null, IClock? clock = null)
    {
        var loader = new CurrentSnapshotLoader();
        var grain = CreateGrain(loader, format, deadlines, now, database, renderer, kinds, state, formatRegistry, clock);
        loader.Current = () => StateOf(grain);
        return grain;
    }

    private static TestSessionGrain CreateGrain(ISessionSnapshotLoader loader, IFormat format, ISessionDeadlineReconciler deadlines, DateTimeOffset now, ITenantDb? database = null, IViewRenderer? renderer = null, IKindRegistry? kinds = null, SessionState? initialState = null, IFormatRegistry? formatRegistry = null, IClock? clock = null)
    {
        var grain = new TestSessionGrain(loader, new SessionStateStore(database ?? new ThrowingDatabase()),
            formatRegistry ?? new FormatRegistry([format]), kinds ?? new KindRegistry([]), clock ?? new TestClock(now), renderer ?? new Renderer(), deadlines,
            NullLogger<SessionGrain>.Instance);
        if (initialState is not null) typeof(SessionGrain).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(grain, initialState);
        return grain;
    }

    private static SessionState State(bool ended = false, DateTimeOffset? retryAt = null, DateTimeOffset? dueAt = null, DateTimeOffset? startedAt = null)
    {
        var position = new Position(0, 0);
        var session = new SessionRow("session", startedAt, dueAt, position, ended, Format: "recording", Version: 3, RetryAt: retryAt, PositionStartedAt: startedAt);
        var item = SessionItemState.Create(new SessionItem(0, "kind", 1, null, null), Json("{}"), Json("{}"), Json("{}"), Json("{}"));
        return SessionState.Create(session, Json("{}"), [new Seat(Player, "player", position)], [item]);
    }

    private static SessionState PollState()
    {
        var position = new Position(0, 0);
        var session = new SessionRow("session", null, null, position, Format: "poll", Version: 3);
        var item = SessionItemState.Create(
            new SessionItem(0, "poll_choice", 1, null, null),
            Json("{\"options\":[{\"id\":\"yes\",\"text\":\"Yes\"},{\"id\":\"no\",\"text\":\"No\"}]}"),
            Json("{}"),
            Json("{}"),
            Json("{}"));
        return SessionState.Create(session, Json("{}"), [new Seat(Player, "player", position), new Seat("second-player", "player", position)], [item]);
    }

    private static SessionCommand Command(DateTimeOffset received, string type = "answer", int slot = 0, string payload = "{}", int? revision = null) =>
        new(Player, type, "0", slot, 0, revision, null, payload, received);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static SessionState? StateOf(SessionGrain grain) => (SessionState?)typeof(SessionGrain).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(grain);

    private static object? DeadlineTimerOf(SessionGrain grain) => typeof(SessionGrain).GetField("_deadlineTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(grain);

    private sealed class CurrentSnapshotLoader : ISessionSnapshotLoader
    {
        public Func<SessionState?>? Current { get; set; }
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Current?.Invoke());
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class SweepSource(IReadOnlyList<DueSession> sessions) : IDueSessionSource
    {
        public Task<IReadOnlyList<DueSession>> FindAsync(DateTimeOffset asOf, int maxRows, IReadOnlyCollection<Guid> skip, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DueSession>>(sessions.Where(session => !skip.Contains(session.SessionId)).Take(maxRows).ToArray());
    }

    private sealed class SweepDispatcher(Func<DueSession, Task> dispatch) : ISessionTimeoutDispatcher
    {
        public Task DispatchAsync(DueSession session) => dispatch(session);
    }

    private sealed class ToggleSnapshotLoader : ISessionSnapshotLoader
    {
        public SessionState? State { get; set; }
        public int LoadCount { get; private set; }
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) { LoadCount++; return Task.FromResult(State); }
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class ThrowingSnapshotLoader : ISessionSnapshotLoader
    {
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromException<SessionState?>(new KnownDatabaseException());
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionItemState>>([]);
    }

    private sealed class TestSessionGrain(
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
        public int DeactivateCount { get; private set; }
        protected override void Deactivate() => DeactivateCount++;
    }

    private sealed class ThrowingDatabase : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) => throw new InvalidOperationException("Store must not be called.");
    }

    private sealed class FailingDatabase(Exception exception) : ITenantDb
    {
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) => Task.FromException<T>(exception);
    }

    private sealed class SavingDatabase : ITenantDb
    {
        private readonly TaskCompletionSource _writeHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdNextWrite;
        public int CallCount { get; private set; }
        public DateTimeOffset? SavedDueAt { get; private set; }
        public Task WriteHeld => _writeHeld.Task;
        public void HoldNextWrite() => _holdNextWrite = 1;
        public void ReleaseHeldWrite() => _releaseWrite.TrySetResult();
        public async Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false)
        {
            if (!readOnly && Interlocked.Exchange(ref _holdNextWrite, 0) == 1)
            {
                _writeHeld.TrySetResult();
                await _releaseWrite.Task.WaitAsync(cancellationToken);
            }
            return await Run(callback);
        }
        private async Task<T> Run<T>(Func<ITenantTx, Task<T>> callback) { CallCount++; return await callback(new SavingTransaction(this)); }

        public void SaveDueAt(object? parameters)
        {
            var value = parameters?.GetType().GetProperty("DueAt")?.GetValue(parameters);
            SavedDueAt = value as DateTimeOffset? ?? (value is DateTimeOffset dueAt ? dueAt : null);
        }
    }

    private sealed class SavingTransaction(SavingDatabase database) : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<T>>([]);
        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (typeof(T) == typeof(long?))
            {
                database.SaveDueAt(parameters);
                return Task.FromResult((T?)(object)1L);
            }
            return typeof(T).Name == "FaultChange"
                ? Task.FromResult((T?)CreateFaultChange(typeof(T)))
                : throw new InvalidOperationException($"Unexpected query type {typeof(T)}.");
        }

        private static object CreateFaultChange(Type type)
        {
            var value = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetProperty("Version")!.SetValue(value, 1L);
            type.GetProperty("Status")!.SetValue(value, "running");
            type.GetProperty("FaultCount")!.SetValue(value, (short)1);
            type.GetProperty("RetryAt")!.SetValue(value, DateTimeOffset.Parse("2026-01-01T00:00:01Z"));
            return value;
        }
    }

    private sealed class KnownDatabaseException : DbException { }

    private sealed class HeldFailingDatabase(Exception exception) : ITenantDb
    {
        private readonly TaskCompletionSource _writeHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _failWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public int WriteCount => Volatile.Read(ref _writes);
        public Task WriteHeld => _writeHeld.Task;
        public void FailHeldWrite() => _failWrite.TrySetResult();
        public async Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false)
        {
            if (!readOnly && Interlocked.Increment(ref _writes) == 1)
            {
                _writeHeld.TrySetResult();
                await _failWrite.Task;
            }
            throw exception;
        }
    }

    private sealed class HoldingDeadlines : ISessionDeadlineReconciler
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdNext;
        public Task Held => _held.Task;
        public void HoldNext() => _holdNext = 1;
        public void Release() => _release.TrySetResult();
        public async Task ReconcileAsync(SessionState state, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                _held.TrySetResult();
                await _release.Task;
            }
        }
    }

    private sealed class RecordingDeadlines : ISessionDeadlineReconciler
    {
        public int Count { get; private set; }
        public Task ReconcileAsync(SessionState state, CancellationToken cancellationToken = default) { Count++; return Task.CompletedTask; }
    }

    private sealed class Renderer : IViewRenderer
    {
        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => new(state.Session.Version, "{}");
    }

    private sealed class CancelledRenderer : IViewRenderer
    {
        public int CancelledCount { get; private set; }
        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => throw new InvalidOperationException("Format rendering must not run for cancelled sessions.");
        public RenderedSessionView RenderCancelled(SessionState state, string viewer, DateTimeOffset now) { CancelledCount++; return new(state.Session.Version, "{}"); }
    }

    private sealed class ThrowingRenderer : IViewRenderer
    {
        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => throw new InvalidOperationException("renderer unavailable");
    }

    private class RecordingFormat : IFormat
    {
        public int ApplyCount { get; private set; }
        public Exception? Rejection { get; init; }
        public string Id => "recording";
        public virtual FormatSpec Spec { get; } = new(Pacing.Shared, 1, 2, false, false, _ => true, false, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public virtual TimeSpan? MaximumDeadlineLateness => null;
        public virtual void Apply(ISession session, Command command) { ApplyCount++; if (Rejection is not null) throw Rejection; }
        public object View(IViewing viewing) => new { };
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(0, []);
        public void Start(ISession session) { }
        public virtual void Timeout(ISession session) { }
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }

    private sealed class MutatingFormat : RecordingFormat
    {
        public override void Apply(ISession session, Command command)
        {
            base.Apply(session, command);
            session.State = Json("{\"changed\":true}");
        }
    }

    private class TimeoutFormat : RecordingFormat
    {
        public int TimeoutCount { get; private set; }
        public override void Timeout(ISession session)
        {
            TimeoutCount++;
            session.State = Json("{\"timedOut\":true}");
        }
    }

    private sealed class ThrowingFormat : RecordingFormat
    {
        public override void Apply(ISession session, Command command) => throw new InvalidOperationException("format bug");
    }

    private sealed class ThrowingTimeoutFormat : RecordingFormat
    {
        public override void Timeout(ISession session) => throw new InvalidOperationException("format should not be invoked");
    }

    private sealed class LatenessFormat(TimeSpan limit) : TimeoutFormat
    {
        public override TimeSpan? MaximumDeadlineLateness => limit;
    }

    private sealed class SlotMovingFormat : RecordingFormat
    {
        public override void Apply(ISession session, Command command) { base.Apply(session, command); session.SetPosition(0, command.At.Slot + 1); }
        public override void Timeout(ISession session) => session.SetPosition(0, 1);
    }

    private sealed class AdvancingFormat : RecordingFormat
    {
        public override FormatSpec Spec { get; } = new(Pacing.Personal, 1, 2, false, false, _ => true, false, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public override void Apply(ISession session, Command command) { base.Apply(session, command); session.SetSeatPosition(command.Player, 0, command.At.Slot + 1); }
    }

    private sealed class AnsweringFormat(TimeSpan grace) : RecordingFormat
    {
        public override FormatSpec Spec { get; } = new(Pacing.Shared, 1, 2, false, false, _ => true, false, null, false, [], grace, new Dictionary<string, Type>());
        public override void Apply(ISession session, Command command) { base.Apply(session, command); session.Record(session.Grade(command), 1); }
    }

    private sealed class RevisionFormat : RecordingFormat
    {
        public override void Apply(ISession session, Command command)
        {
            base.Apply(session, command);
            var prior = session.Answer(command.Player, command.At.Slot);
            if (prior is not null && command.Revision < prior.Revision) return;
            var graded = session.Grade(command);
            if (prior is null) { session.Record(graded, 0); return; }
            if (command.Revision == prior.Revision)
            {
                if (prior.Value is JsonElement previous && graded.Stored is JsonElement current
                    && JsonElement.DeepEquals(previous, current)) return;
                throw new SessionCommandException("revision_conflict");
            }
            session.Replace(graded, command.Revision);
        }
    }

    private sealed class EchoKind : IKindRuntime
    {
        public string Id => "kind";
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
}
