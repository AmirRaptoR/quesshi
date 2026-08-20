namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IQuestionGeneratorGrain")]
public interface IQuestionGeneratorGrain : IGrainWithIntegerKey
{
    /// <summary>Registers or removes the nightly reminder to match configuration.</summary>
    [Alias("ApplyScheduleAsync")]
    Task ApplyScheduleAsync(bool nightly);
    [Alias("RunNowAsync")]
    Task<string> RunNowAsync();
}
