using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Engine.Contracts;
using Quesshi.Server.Sessions;

namespace Quesshi.Server.Tests;

public sealed class DeadlineSweepTests
{
    [Fact]
    public async Task A_stuck_delivery_does_not_delay_the_other_due_sessions()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var sessions = Enumerable.Range(0, 51).Select(index => new DueSession(Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(-1))).ToArray();
        var stuck = sessions[0];
        var source = new RecordingSource(sessions);
        var dispatcher = new RecordingDispatcher(session => session.SessionId == stuck.SessionId
            ? new TaskCompletionSource().Task
            : Task.CompletedTask);
        var sweep = new DeadlineSweep(source, dispatcher, new TestClock(now), NullLogger<DeadlineSweep>.Instance);

        await sweep.RunPassAsync();

        Assert.Equal(51, dispatcher.Deliveries.Count);
        Assert.Contains(stuck.SessionId, dispatcher.Deliveries);
        Assert.Equal(256, source.Requests.Single().MaxRows);
    }

    [Fact]
    public async Task A_failed_delivery_is_held_in_memory_and_skipped_on_the_next_pass()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var due = new DueSession(Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(-1));
        var source = new RecordingSource([due]);
        var dispatcher = new RecordingDispatcher(_ => Task.FromException(new TimeoutException()));
        var sweep = new DeadlineSweep(source, dispatcher, new TestClock(now), NullLogger<DeadlineSweep>.Instance);

        await sweep.RunPassAsync();
        await sweep.RunPassAsync();

        Assert.Single(dispatcher.Deliveries);
        Assert.Contains(due.SessionId, source.Requests[1].Skip);
    }

    [Fact]
    public async Task A_cancelled_delivery_is_held_then_retried_after_backoff()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var due = new DueSession(Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(-1));
        var clock = new TestClock(now);
        var source = new RecordingSource([due]);
        var attempts = 0;
        var dispatcher = new RecordingDispatcher(_ => ++attempts == 1
            ? Task.FromCanceled(new CancellationToken(canceled: true))
            : Task.CompletedTask);
        var sweep = new DeadlineSweep(source, dispatcher, clock, NullLogger<DeadlineSweep>.Instance);

        await sweep.RunPassAsync();
        await sweep.RunPassAsync();

        Assert.Single(dispatcher.Deliveries);
        Assert.Contains(due.SessionId, source.Requests[1].Skip);

        clock.Advance(TimeSpan.FromSeconds(1));
        await sweep.RunPassAsync();
        source.Sessions = [];
        await sweep.RunPassAsync();

        Assert.Equal(2, dispatcher.Deliveries.Count);
    }

    [Fact]
    public async Task A_pass_never_starts_more_than_256_deliveries()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var source = new RecordingSource(Enumerable.Range(0, 300)
            .Select(_ => new DueSession(Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(-1))).ToArray());
        var dispatcher = new RecordingDispatcher(_ => Task.CompletedTask);
        var sweep = new DeadlineSweep(source, dispatcher, new TestClock(now), NullLogger<DeadlineSweep>.Instance);

        await sweep.RunPassAsync();

        Assert.Equal(DeadlineSweep.MaximumInFlight, dispatcher.Deliveries.Count);
    }

    private sealed class RecordingSource(IReadOnlyList<DueSession> sessions) : IDueSessionSource
    {
        public List<Request> Requests { get; } = [];
        public IReadOnlyList<DueSession> Sessions { get; set; } = sessions;

        public Task<IReadOnlyList<DueSession>> FindAsync(DateTimeOffset asOf, int maxRows, IReadOnlyCollection<Guid> skip, CancellationToken cancellationToken)
        {
            Requests.Add(new(maxRows, skip.ToArray()));
            return Task.FromResult<IReadOnlyList<DueSession>>(Sessions.Where(session => !skip.Contains(session.SessionId)).Take(maxRows).ToArray());
        }
    }

    private sealed record Request(int MaxRows, IReadOnlyCollection<Guid> Skip);

    private sealed class RecordingDispatcher(Func<DueSession, Task> dispatch) : ISessionTimeoutDispatcher
    {
        public List<Guid> Deliveries { get; } = [];

        public Task DispatchAsync(DueSession session)
        {
            Deliveries.Add(session.SessionId);
            return dispatch(session);
        }
    }
}
