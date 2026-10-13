using Quesshi.Application.Jobs;
using Quesshi.Server.Health;

namespace Quesshi.Server.Jobs;

/// <summary>Polls for due durable jobs and runs them with at most four concurrent handlers per silo.</summary>
internal sealed class JobWorkerService(
    JobWorker worker,
    IBackgroundLoopRegistry loops,
    ILogger<JobWorkerService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    internal const string LoopName = "job-worker";
    internal const int BatchSize = JobWorker.MaximumConcurrentHandlers;
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IBackgroundLoop _loop = loops.Register(LoopName);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly List<Task> _running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    _running.AddRange(await worker.StartPassAsync(BatchSize, LeaseDuration, stoppingToken));
                    _running.RemoveAll(task => task.IsCompleted);
                    _loop.Passed();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Job worker pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
