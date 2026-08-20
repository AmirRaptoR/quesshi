namespace Quesshi.Application.UseCases;

public sealed class TopUpOptions
{
    /// <summary>
    /// Run the top-up on a nightly schedule. Off by default: a job that spends money and, with
    /// AutoApprove on, publishes to players unwatched should be switched on deliberately. The
    /// admin panel's button works either way.
    /// </summary>
    public bool Nightly { get; set; }

    /// <summary>How many questions each (language, category, level) bucket should hold before we stop generating.</summary>
    public int TargetPerBucket { get; set; } = 25;
    public int MaxPerRun { get; set; } = 60;
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Publish generated questions straight away rather than parking them for review. Reviewing
    /// every question by hand does not scale; players reporting the bad ones does. Set false to go
    /// back to reviewing everything up front.
    /// </summary>
    public bool AutoApprove { get; set; } = true;

}
