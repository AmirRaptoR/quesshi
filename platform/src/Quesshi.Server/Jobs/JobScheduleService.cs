using Quesshi.Application.Jobs;
using Quesshi.Server.Health;

namespace Quesshi.Server.Jobs;

/// <summary>Enqueues recurring durable jobs for tenants with their owning plugin enabled.</summary>
internal sealed class JobScheduleService(
    JobScheduler scheduler,
    IBackgroundLoopRegistry loops,
    ILogger<JobScheduleService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    internal const string LoopName = "job-scheduler";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IBackgroundLoop _loop = loops.Register(LoopName);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    await scheduler.RunPassAsync(stoppingToken);
                    _loop.Passed();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Job scheduler pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
