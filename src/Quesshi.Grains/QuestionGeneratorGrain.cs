using Microsoft.Extensions.Logging;
using Orleans;
using Quesshi.Grains.Abstractions;
using Orleans.Runtime;
using Quesshi.Application.UseCases;

namespace Quesshi.Grains;

/// <summary>
/// The scheduled job. An Orleans reminder rather than a hosted service, so it survives restarts
/// and fires exactly once even if the app is running on more than one node later.
/// </summary>
public sealed class QuestionGeneratorGrain(TopUpQuestionBank topUp, ILogger<QuestionGeneratorGrain> logger)
    : Grain, IQuestionGeneratorGrain, IRemindable
{
    private const string Daily = "daily-top-up";

    public async Task ApplyScheduleAsync(bool nightly)
    {
        if (nightly)
        {
            await this.RegisterOrUpdateReminder(Daily, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));
            return;
        }

        // Reminders live in Redis, not in this process: one registered by an earlier run outlives
        // the setting that created it, so turning the switch off has to actively remove it.
        if (await this.GetReminder(Daily) is { } existing)
        {
            await this.UnregisterReminder(existing);
            logger.LogInformation("Nightly question top-up is off; removed the reminder.");
        }
    }

    public async Task<string> RunNowAsync()
    {
        var run = await topUp.RunAsync();
        return run.Id;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        var run = await topUp.RunAsync();
        logger.LogInformation("Question top-up finished: {Inserted} inserted, {Rejected} rejected, error={Error}",
            run.Inserted, run.Rejected, run.Error ?? "none");
    }
}
