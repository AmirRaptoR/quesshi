using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Clock;

namespace Quesshi.Infrastructure.Tests;

public sealed class ClockContractTests
{
    [Fact]
    public void Test_clock_is_controllable()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(3), clock.Now);
    }

    [Fact]
    public async Task Calibration_selects_the_shortest_round_trip_and_disciplines_now_to_the_database()
    {
        var monotonic = new ManualMonotonicClock();
        var database = new LatencyInjectingDatabaseClock(monotonic, TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        await using var clock = new PostgresClock(database, monotonic);

        Assert.True(await clock.CalibrateAsync());

        Assert.Equal(TimeSpan.FromMilliseconds(5), clock.Uncertainty);
        var databaseNow = DateTimeOffset.UnixEpoch.AddSeconds(3).AddMilliseconds(monotonic.Timestamp);
        Assert.True((clock.Now - databaseNow).Duration() < clock.Uncertainty);
        Assert.True(clock.IsHealthy);
    }

    [Fact]
    public async Task Health_is_unhealthy_before_success_above_fifty_milliseconds_and_after_five_minutes()
    {
        var uncalibratedMonotonic = new ManualMonotonicClock();
        await using var uncalibrated = new PostgresClock(new FailingDatabaseClock(), uncalibratedMonotonic);
        Assert.False(await uncalibrated.CalibrateAsync());
        Assert.False(uncalibrated.IsHealthy);
        Assert.Throws<InvalidOperationException>(() => _ = uncalibrated.Now);

        var boundaryMonotonic = new ManualMonotonicClock();
        await using var boundary = new PostgresClock(new LatencyInjectingDatabaseClock(boundaryMonotonic, TimeSpan.Zero, Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 5).ToArray()), boundaryMonotonic);
        Assert.True(await boundary.CalibrateAsync());
        Assert.Equal(TimeSpan.FromMilliseconds(50), boundary.Uncertainty);
        Assert.True(boundary.IsHealthy);
        boundaryMonotonic.Advance(TimeSpan.FromMinutes(5));
        Assert.True(boundary.IsHealthy);
        boundaryMonotonic.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(boundary.IsHealthy);

        var uncertainMonotonic = new ManualMonotonicClock();
        await using var uncertain = new PostgresClock(new LatencyInjectingDatabaseClock(uncertainMonotonic, TimeSpan.Zero, Enumerable.Repeat(TimeSpan.FromMilliseconds(102), 5).ToArray()), uncertainMonotonic);
        Assert.True(await uncertain.CalibrateAsync());
        Assert.False(uncertain.IsHealthy);
    }

    [Fact]
    public async Task Failed_calibration_preserves_the_last_success_and_can_be_retried()
    {
        var monotonic = new ManualMonotonicClock();
        var database = new FailingAfterFirstDatabaseClock(monotonic);
        await using var clock = new PostgresClock(database, monotonic);

        Assert.True(await clock.CalibrateAsync());
        var uncertainty = clock.Uncertainty;
        var age = clock.LastSuccessfulCalibrationAge;
        monotonic.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await clock.CalibrateAsync());

        Assert.Equal(uncertainty, clock.Uncertainty);
        Assert.True(clock.LastSuccessfulCalibrationAge > age);
        Assert.True(await clock.CalibrateAsync());
    }

    [Fact]
    public async Task Startup_and_periodic_calibration_take_five_samples_and_retry_after_a_failure()
    {
        var monotonic = new ManualMonotonicClock();
        var database = new PeriodicDatabaseClock(monotonic);
        await using var clock = new PostgresClock(database, monotonic, TimeSpan.FromMilliseconds(10));

        await clock.StartAsync();
        await database.RetrySucceeded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(clock.IsHealthy);
        Assert.Equal(11, database.ReadCount);
    }

    [Fact]
    public async Task A_new_clock_instance_is_uncalibrated_even_when_another_instance_succeeded()
    {
        var monotonic = new ManualMonotonicClock();
        await using var calibrated = new PostgresClock(new LatencyInjectingDatabaseClock(monotonic, TimeSpan.Zero, Enumerable.Repeat(TimeSpan.Zero, 5).ToArray()), monotonic);
        Assert.True(await calibrated.CalibrateAsync());

        await using var restarted = new PostgresClock(new FailingDatabaseClock(), new ManualMonotonicClock());

        Assert.False(restarted.HasCalibration);
        Assert.False(new ClockReadiness(restarted).IsReady);
        Assert.Equal("clock_unavailable", new ClockAdmission(restarted).Admit().RefusalCode);
    }

    [Fact]
    public void Readiness_and_scored_command_admission_follow_clock_health()
    {
        IClockHealth unhealthy = new TestClockHealth(isHealthy: false);
        IClockHealth healthy = new TestClockHealth(isHealthy: true);

        Assert.False(new ClockReadiness(unhealthy).IsReady);
        Assert.Equal("clock_unavailable", new ClockAdmission(unhealthy).Admit().RefusalCode);
        Assert.True(new ClockReadiness(healthy).IsReady);
        Assert.True(new ClockAdmission(healthy).Admit().IsAllowed);
    }

    private sealed class TestClockHealth(bool isHealthy) : IClockHealth
    {
        public bool HasCalibration => isHealthy;
        public TimeSpan? Uncertainty => isHealthy ? TimeSpan.Zero : null;
        public TimeSpan? LastSuccessfulCalibrationAge => isHealthy ? TimeSpan.Zero : null;
        public bool IsHealthy => isHealthy;
    }

    private sealed class ManualMonotonicClock : IMonotonicClock
    {
        public long Timestamp { get; private set; }
        public long Frequency => 1_000;

        public void Advance(TimeSpan duration) => Timestamp += duration.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private sealed class LatencyInjectingDatabaseClock(ManualMonotonicClock monotonic, TimeSpan offset, params TimeSpan[] roundTrips) : IDatabaseClock
    {
        private readonly Queue<TimeSpan> _roundTrips = new(roundTrips);

        public Task<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default)
        {
            var roundTrip = _roundTrips.Count > 0 ? _roundTrips.Dequeue() : TimeSpan.Zero;
            var before = monotonic.Timestamp;
            monotonic.Advance(roundTrip);
            return Task.FromResult(DateTimeOffset.UnixEpoch.Add(offset).AddMilliseconds(before + (roundTrip.TotalMilliseconds / 2)));
        }
    }

    private sealed class FailingDatabaseClock : IDatabaseClock
    {
        public Task<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default) => Task.FromException<DateTimeOffset>(new InvalidOperationException("database unavailable"));
    }

    private sealed class FailingAfterFirstDatabaseClock(ManualMonotonicClock monotonic) : IDatabaseClock
    {
        private int _attempt;

        public Task<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (++_attempt == 6) return Task.FromException<DateTimeOffset>(new InvalidOperationException("database unavailable"));
            return Task.FromResult(DateTimeOffset.UnixEpoch.AddMilliseconds(monotonic.Timestamp));
        }
    }

    private sealed class PeriodicDatabaseClock(ManualMonotonicClock monotonic) : IDatabaseClock
    {
        public TaskCompletionSource RetrySucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }

        public Task<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadCount == 6) return Task.FromException<DateTimeOffset>(new InvalidOperationException("database unavailable"));
            monotonic.Advance(TimeSpan.FromMilliseconds(1));
            if (ReadCount == 11) RetrySucceeded.TrySetResult();
            return Task.FromResult(DateTimeOffset.UnixEpoch.AddMilliseconds(monotonic.Timestamp));
        }
    }
}
