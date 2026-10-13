using System.Reflection;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;
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
    public async Task Format_rejection_and_late_handling_return_current_view_without_apply()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:10Z");
        var rejectedFormat = new RecordingFormat { Rejection = new InvalidOperationException("unsupported") };
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
        var grain = CreateGrain(new ThrowingSnapshotLoader(), new MutatingFormat(), new RecordingDeadlines(), now,
            database: new SavingDatabase(), initialState: State());

        var result = await grain.ExecuteAsync(Command(now));

        Assert.Equal("view_unavailable", result.Code);
        Assert.True(result.Retryable);
        Assert.Null(StateOf(grain));
        Assert.Equal(1, grain.DeactivateCount);
    }

    [Fact]
    public async Task Stale_and_unknown_commit_outcomes_discard_the_cached_snapshot()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var stale = CreateGrain(State(), new MutatingFormat(), new RecordingDeadlines(), now, database: new FailingDatabase(new StaleActivationException()));

        var staleResult = await stale.ExecuteAsync(Command(now));

        Assert.Equal("stale_activation", staleResult.Code);
        Assert.True(staleResult.Retryable);
        Assert.Null(StateOf(stale));
        Assert.Equal(1, stale.DeactivateCount);

        var unknown = CreateGrain(State(), new MutatingFormat(), new RecordingDeadlines(), now,
            database: new FailingDatabase(new TenantCommitOutcomeUnknownException(new IOException("connection lost"))));

        var unknownResult = await unknown.ExecuteAsync(Command(now));

        Assert.Equal("retryable", unknownResult.Code);
        Assert.True(unknownResult.Retryable);
        Assert.Null(StateOf(unknown));
        Assert.Equal(1, unknown.DeactivateCount);
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

    private static TestSessionGrain CreateGrain(SessionState state, IFormat format, ISessionDeadlineReconciler deadlines, DateTimeOffset now, ITenantDb? database = null, IViewRenderer? renderer = null, IKindRegistry? kinds = null)
    {
        var loader = new CurrentSnapshotLoader();
        var grain = CreateGrain(loader, format, deadlines, now, database, renderer, kinds, state);
        loader.Current = () => StateOf(grain);
        return grain;
    }

    private static TestSessionGrain CreateGrain(ISessionSnapshotLoader loader, IFormat format, ISessionDeadlineReconciler deadlines, DateTimeOffset now, ITenantDb? database = null, IViewRenderer? renderer = null, IKindRegistry? kinds = null, SessionState? initialState = null)
    {
        var grain = new TestSessionGrain(loader, new SessionStateStore(database ?? new ThrowingDatabase()),
            new FormatRegistry([format]), kinds ?? new KindRegistry([]), new TestClock(now), renderer ?? new Renderer(), deadlines,
            NullLogger<SessionGrain>.Instance);
        if (initialState is not null) typeof(SessionGrain).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(grain, initialState);
        return grain;
    }

    private static SessionState State(bool ended = false, DateTimeOffset? retryAt = null, DateTimeOffset? dueAt = null, DateTimeOffset? startedAt = null)
    {
        var position = new Position(0, 0);
        var session = new SessionRow("session", null, dueAt, position, ended, Format: "recording", Version: 3, RetryAt: retryAt, PositionStartedAt: startedAt);
        var item = SessionItemState.Create(new SessionItem(0, "kind", 1, null, null), Json("{}"), Json("{}"), Json("{}"), Json("{}"));
        return SessionState.Create(session, Json("{}"), [new Seat(Player, "player", position)], [item]);
    }

    private static SessionCommand Command(DateTimeOffset received, string type = "answer", int slot = 0, string payload = "{}", int? revision = null) =>
        new(Player, type, "0", slot, 0, revision, null, payload, received);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static SessionState? StateOf(SessionGrain grain) => (SessionState?)typeof(SessionGrain).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(grain);

    private sealed class CurrentSnapshotLoader : ISessionSnapshotLoader
    {
        public Func<SessionState?>? Current { get; set; }
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Current?.Invoke());
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
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
        public int CallCount { get; private set; }
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) => Run(callback);
        private async Task<T> Run<T>(Func<ITenantTx, Task<T>> callback) { CallCount++; return await callback(new SavingTransaction()); }
    }

    private sealed class SavingTransaction : ITenantTx
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<T>>([]);
        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            typeof(T) == typeof(long?)
                ? Task.FromResult((T?)(object)1L)
                : throw new InvalidOperationException($"Unexpected query type {typeof(T)}.");
    }

    private sealed class KnownDatabaseException : DbException { }

    private sealed class RecordingDeadlines : ISessionDeadlineReconciler
    {
        public int Count { get; private set; }
        public Task ReconcileAsync(SessionState state, CancellationToken cancellationToken = default) { Count++; return Task.CompletedTask; }
    }

    private sealed class Renderer : IViewRenderer
    {
        public RenderedSessionView Render(SessionState state, IFormat format, string viewer, DateTimeOffset now) => new(state.Session.Version, "{}");
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
        public virtual void Apply(ISession session, Command command) { ApplyCount++; if (Rejection is not null) throw Rejection; }
        public object View(IViewing viewing) => new { };
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(0, []);
        public void Start(ISession session) { }
        public void Timeout(ISession session) { }
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
