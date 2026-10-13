using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Server.Health;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quesshi.Server.Tests;

public sealed class PlatformHealthTests
{
    [Fact]
    public async Task Database_probe_returns_false_when_the_database_is_unreachable()
    {
        await using var sources = new DatabaseSources(
            "Host=127.0.0.1;Port=1;Database=unreachable;Timeout=1;Command Timeout=1",
            "Host=127.0.0.1;Port=1;Database=unreachable;Timeout=1;Command Timeout=1");
        var probe = new PlatformDatabaseHealth(sources, NullLogger<PlatformDatabaseHealth>.Instance);

        Assert.False(await probe.CanReachAsync(default));
    }

    [Fact]
    public async Task Check_reports_each_dependency_and_rejects_each_unhealthy_state()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-13T00:00:00Z"));
        var loops = new BackgroundLoopRegistry(time);
        var database = new DatabaseHealthProbe(true);
        var clock = new ClockHealth(true);
        var silo = new SiloHealth(true);
        var health = new PlatformHealth(database, clock, silo, loops);

        Assert.True((await health.CheckAsync(default)).IsHealthy);

        database.IsReachable = false;
        Assert.False((await health.CheckAsync(default)).IsHealthy);
        database.IsReachable = true;

        clock.IsHealthy = false;
        Assert.False((await health.CheckAsync(default)).IsHealthy);
        clock.IsHealthy = true;

        silo.IsActive = false;
        Assert.False((await health.CheckAsync(default)).IsHealthy);
        silo.IsActive = true;

        var loop = loops.Register("job-worker");
        var neverPassed = await health.CheckAsync(default);
        Assert.False(neverPassed.IsHealthy);
        Assert.Null(Assert.Single(neverPassed.Loops).LastSuccessfulPassAge);

        loop.Passed();
        Assert.True((await health.CheckAsync(default)).IsHealthy);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True((await health.CheckAsync(default)).IsHealthy);
        time.Advance(TimeSpan.FromSeconds(1));
        var stale = await health.CheckAsync(default);
        Assert.False(stale.IsHealthy);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(stale.Loops).LastSuccessfulPassAge);
    }

    private sealed class DatabaseHealthProbe(bool isReachable) : IPlatformDatabaseHealth
    {
        public bool IsReachable { get; set; } = isReachable;
        public Task<bool> CanReachAsync(CancellationToken cancellationToken) => Task.FromResult(IsReachable);
    }

    private sealed class ClockHealth(bool isHealthy) : IClockHealth
    {
        public bool IsHealthy { get; set; } = isHealthy;
        public bool HasCalibration => IsHealthy;
        public TimeSpan? Uncertainty => null;
        public TimeSpan? LastSuccessfulCalibrationAge => null;
    }

    private sealed class SiloHealth(bool isActive) : IPlatformSiloHealth
    {
        public bool IsActive { get; set; } = isActive;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
