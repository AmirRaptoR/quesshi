using System.Data;
using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;

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
        public List<string> Commands { get; } = [];
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<T>>([]);
        public Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) { Commands.Add(sql); return Task.FromResult(1); }
        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Queries.Add(sql);
            return Task.FromResult((T?)(object)(long?)5);
        }
    }
}
