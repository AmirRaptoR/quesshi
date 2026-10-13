using System.Reflection;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;

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

        var committed = CreateGrain(State(), new MutatingFormat(), new RecordingDeadlines(), now, database: new SavingDatabase(), renderer: new ThrowingRenderer());
        var unavailable = await committed.ExecuteAsync(Command(now));

        Assert.Equal("view_unavailable", unavailable.Code);
        Assert.Equal(1, StateOf(committed)!.Session.Version);
    }

    private static SessionGrain CreateGrain(SessionState state, IFormat format, ISessionDeadlineReconciler deadlines, DateTimeOffset now, ITenantDb? database = null, IViewRenderer? renderer = null)
    {
        var grain = new SessionGrain(new SnapshotLoader(), new SessionStateStore(database ?? new ThrowingDatabase()),
            new FormatRegistry([format]), null!, new TestClock(now), renderer ?? new Renderer(), deadlines,
            NullLogger<SessionGrain>.Instance);
        typeof(SessionGrain).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(grain, state);
        return grain;
    }

    private static SessionState State(bool ended = false, DateTimeOffset? retryAt = null, DateTimeOffset? dueAt = null)
    {
        var position = new Position(0, 0);
        var session = new SessionRow("session", null, dueAt, position, ended, Format: "recording", Version: 3, RetryAt: retryAt);
        var item = SessionItemState.Create(new SessionItem(0, "kind", 1, null, null), Json("{}"), Json("{}"), Json("{}"), Json("{}"));
        return SessionState.Create(session, Json("{}"), [new Seat(Player, "player", position)], [item]);
    }

    private static SessionCommand Command(DateTimeOffset received, string type = "answer") =>
        new(Player, type, "0", 0, 0, null, null, "{}", received);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static SessionState? StateOf(SessionGrain grain) => (SessionState?)typeof(SessionGrain).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(grain);

    private sealed class SnapshotLoader : ISessionSnapshotLoader
    {
        public Task<SessionState?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<SessionState?>(null);
        public Task<IReadOnlyList<SessionItemState>> LoadItemsAsync(Guid tenantId, Guid sessionId, IReadOnlyCollection<int> slots, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionItemState>>([]);
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
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false) => callback(new SavingTransaction());
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
        public SessionView Render(SessionState state, IFormat format, string viewer) => new(state.Session.Version, "{}");
    }

    private sealed class ThrowingRenderer : IViewRenderer
    {
        public SessionView Render(SessionState state, IFormat format, string viewer) => throw new InvalidOperationException("renderer unavailable");
    }

    private class RecordingFormat : IFormat
    {
        public int ApplyCount { get; private set; }
        public Exception? Rejection { get; init; }
        public string Id => "recording";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 2, false, false, _ => true, false, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
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
}
