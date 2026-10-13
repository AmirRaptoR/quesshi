using Quesshi.Application.Jobs;

namespace Quesshi.Application.Tests;

public sealed class JobScheduleRegistryTests
{
    [Fact]
    public void Every_registers_each_handler_schedule_once()
    {
        var handlers = new JobHandlerRegistry();
        handlers.Register("plugin", "sweep", (_, _) => Task.CompletedTask);
        var schedules = new JobScheduleRegistry(handlers);

        schedules.Every("plugin", TimeSpan.FromMinutes(1), "sweep");

        var schedule = Assert.Single(schedules.Schedules);
        Assert.Equal("plugin", schedule.Owner);
        Assert.Equal("sweep", schedule.Kind);
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Interval);
        Assert.Throws<InvalidOperationException>(() => schedules.Every("plugin", TimeSpan.FromMinutes(1), "sweep"));

        schedules.Every("plugin", TimeSpan.FromMinutes(2), "sweep");
        Assert.Equal(2, schedules.Schedules.Count);
    }

    [Fact]
    public void Every_requires_a_registered_handler_and_positive_interval()
    {
        var schedules = new JobScheduleRegistry(new JobHandlerRegistry());

        Assert.Throws<ArgumentOutOfRangeException>(() => schedules.Every("plugin", TimeSpan.Zero, "sweep"));
        Assert.Throws<InvalidOperationException>(() => schedules.Every("plugin", TimeSpan.FromMinutes(1), "sweep"));
    }

    [Fact]
    public void Window_start_uses_the_current_utc_interval_boundary()
    {
        var window = JobScheduler.WindowStart(
            DateTimeOffset.Parse("2026-10-13T12:01:59+02:00"),
            TimeSpan.FromMinutes(1));

        Assert.Equal(DateTimeOffset.Parse("2026-10-13T10:01:00Z"), window);
    }
}
