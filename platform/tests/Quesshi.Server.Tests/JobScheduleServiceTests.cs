using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Jobs;
using Quesshi.Server.Health;
using Quesshi.Server.Jobs;
using System.Text.Json;

namespace Quesshi.Server.Tests;

public sealed class JobScheduleServiceTests
{
    [Fact]
    public async Task A_successful_pass_is_visible_to_background_loop_health()
    {
        var handlers = new JobHandlerRegistry();
        var loops = new BackgroundLoopRegistry();
        var service = new JobScheduleService(
            new JobScheduler(new JobScheduleRegistry(handlers), new NoTenants(), new UnusedScheduledJobs()),
            loops,
            NullLogger<JobScheduleService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var loop = await WaitForPassAsync(loops);
            Assert.Equal(JobScheduleService.LoopName, loop.Name);
            Assert.True(loop.IsHealthy);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static async Task<BackgroundLoopStatus> WaitForPassAsync(IBackgroundLoopRegistry loops)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        BackgroundLoopStatus loop;
        do
        {
            loop = Assert.Single(loops.Snapshot());
            if (loop.IsHealthy) return loop;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        while (DateTimeOffset.UtcNow < deadline);

        return loop;
    }

    private sealed class NoTenants : IJobScheduleTenants
    {
        public Task<IReadOnlyList<Guid>> ListEnabledAsync(string owner, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class UnusedScheduledJobs : ISystemScheduledJobs
    {
        public Task<bool> EnqueueOnceAsync(Guid tenantId, JobSchedule schedule, JsonElement payload, string uniqueKey, DateTimeOffset window, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No schedules are registered.");
    }
}
