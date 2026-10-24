using System.Data;
using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Tests;

public sealed class SessionStateStoreTests
{
    [Fact]
    public async Task Version_fence_precedes_answer_and_notification_writes()
    {
        var database = new RecordingTenantDb();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = Guid.NewGuid().ToString();
        var state = SessionState.Create(
            new SessionRow(session.ToString(), null, null, new Position(0, 0), Format: "poll", Version: 4), Json("{}"),
            [new Seat(player, "player", new Position(0, 0))], []) with
        {
            Answers = new Dictionary<AnswerKey, AnswerRecord>
            {
                [new(player, 0)] = new(Json("{\"choice\":\"a\"}"), true, 1, 10, TimeSpan.FromSeconds(2), DateTimeOffset.UnixEpoch, Revision: 2),
            },
        };

        var version = await new SessionStateStore(database).SaveAsync(tenant, session, 4, state);

        Assert.Equal(5, version);
        Assert.Equal(IsolationLevel.ReadCommitted, database.IsolationLevel);
        Assert.StartsWith("update sessions set version = version + 1", database.Transaction.Queries[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(database.Transaction.Commands, sql => sql.StartsWith("insert into session_answers", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("select pg_notify", database.Transaction.Commands[^1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_writes_only_answer_and_seat_rows_changed_by_the_transition()
    {
        var database = new RecordingTenantDb();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var row = new SessionRow(session.ToString(), null, null, new Position(0, 0), Format: "poll", Version: 4);
        var initial = SessionState.Create(row, Json("{}"), [new Seat(first, "player", new Position(0, 0)), new Seat(second, "player", new Position(0, 0))], []) with
        {
            Answers = new Dictionary<AnswerKey, AnswerRecord>
            {
                [new(first, 0)] = new(Json("{\"choice\":\"a\"}"), true, 1, 10, TimeSpan.FromSeconds(2), DateTimeOffset.UnixEpoch, Revision: 1),
            },
        };
        var current = initial with
        {
            Seats = new Dictionary<string, Seat>(initial.Seats) { [first] = initial.Seats[first] with { Score = 20 } },
            Answers = new Dictionary<AnswerKey, AnswerRecord>(initial.Answers)
            {
                [new(first, 0)] = initial.Answers[new(first, 0)] with { Revision = 2 },
            },
        };

        await new SessionStateStore(database).SaveAsync(tenant, session, 4, initial, current);

        Assert.Equal(3, database.Transaction.Commands.Count);
        Assert.Single(database.Transaction.Commands, sql => sql.StartsWith("insert into session_answers", StringComparison.OrdinalIgnoreCase));
        Assert.Single(database.Transaction.Commands, sql => sql.StartsWith("update session_participants", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Core_ending_resets_seats_and_emits_the_terminal_event_in_its_transaction()
    {
        var database = new RecordingTenantDb();
        var events = new RecordingEvents();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();

        var ended = await new SessionStateStore(database, events).EndCoreAsync(tenant, session, CoreEndReason.Fault);

        Assert.True(ended);
        Assert.Contains(database.Transaction.Queries, sql => sql.Contains("status = 'cancelled'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(database.Transaction.Commands, sql => sql.Contains("outcome = 'none', ranked = false", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([session], events.SessionIds);
    }

    [Fact]
    public async Task Format_no_contest_uses_the_schema_end_reason()
    {
        var database = new RecordingTenantDb();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = Guid.NewGuid().ToString();
        var initial = SessionState.Create(
            new SessionRow(session.ToString(), null, null, new Position(0, 0), Format: "poll", Version: 4), Json("{}"),
            [new Seat(player, "player", new Position(0, 0))], []);
        var current = initial with { Session = initial.Session with { Ended = true, Status = "finished", EndReason = EndReason.NoContest } };

        await new SessionStateStore(database).SaveAsync(tenant, session, 4, initial, current);

        var parameters = database.Transaction.QueryParameters[0]!;
        var values = parameters.GetType();
        Assert.Equal("no_contest", values.GetProperty("endReason")!.GetValue(parameters));
    }

    [Fact]
    public async Task Save_persists_eligibility_when_a_player_begins_a_run()
    {
        var database = new RecordingTenantDb();
        var tenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var player = Guid.NewGuid().ToString();
        var initial = SessionState.Create(
            new SessionRow(session.ToString(), null, null, new Position(0, 0), Format: "poll", Version: 4), Json("{}"),
            [new Seat(player, "player", new Position(0, 0))], []);
        var current = initial with { Seats = new Dictionary<string, Seat>(initial.Seats) { [player] = initial.Seats[player] with { Eligible = true } } };

        await new SessionStateStore(database).SaveAsync(tenant, session, 4, initial, current);

        var command = Assert.Single(database.Transaction.Commands, sql => sql.StartsWith("update session_participants", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("eligible = r.eligible", command, StringComparison.OrdinalIgnoreCase);
        var rows = (string)database.Transaction.CommandParameters[database.Transaction.Commands.IndexOf(command)]!.GetType().GetProperty("rows")!.GetValue(database.Transaction.CommandParameters[database.Transaction.Commands.IndexOf(command)])!;
        Assert.Contains("\"eligible\":true", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void Eligible_account_players_receive_ranked_terminal_results()
    {
        var player = Guid.NewGuid().ToString();
        var initial = SessionState.Create(
            new SessionRow("session", DateTimeOffset.UnixEpoch, null, null, Format: "poll"), Json("{}"),
            [new Seat(player, "player")], []);
        var transition = new SessionTransition(initial, new KindRegistry([]), new TestClock(DateTimeOffset.UnixEpoch), TimeSpan.Zero, scored: true);

        transition.SetPosition(0, 0);
        transition.End(EndReason.Completed, [new Standing(player, 1, "win")], Json("{}"));

        Assert.True(transition.Current.Seats[player].Eligible);
        Assert.True(transition.Current.Seats[player].Ranked);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class RecordingTenantDb : ITenantDb
    {
        public RecordingTx Transaction { get; } = new();
        public IsolationLevel IsolationLevel { get; private set; }
        public Task<T> InTenantAsync<T>(Guid tenantId, Func<ITenantTx, Task<T>> callback, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default, bool readOnly = false)
        {
            IsolationLevel = isolationLevel;
            return callback(Transaction);
        }
    }

    private sealed class RecordingTx : ITenantTx
    {
        public List<string> Queries { get; } = [];
        public List<object?> QueryParameters { get; } = [];
        public List<string> Commands { get; } = [];
        public List<object?> CommandParameters { get; } = [];
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<T>>([]);
        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) { Commands.Add(sql); CommandParameters.Add(parameters); return Task.FromResult(1); }
        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Queries.Add(sql);
            QueryParameters.Add(parameters);
            return Task.FromResult((T?)(object)(long?)5);
        }
    }

    private sealed class RecordingEvents : IEvents
    {
        public List<Guid> SessionIds { get; } = [];
        public Task EmitAsync<TEvent>(ITenantTx transaction, TEvent @event, CancellationToken cancellationToken = default)
        {
            if (@event is SessionEnded ended) SessionIds.Add(ended.SessionId);
            return Task.CompletedTask;
        }
    }
}
