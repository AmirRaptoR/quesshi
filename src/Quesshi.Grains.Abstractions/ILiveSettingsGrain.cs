namespace Quesshi.Grains.Abstractions;

/// <summary>
/// The live-duel kill switch: one grain, one flag, following the same runtime-toggle shape as
/// <see cref="IQuestionGeneratorGrain.ApplyScheduleAsync"/>. Off means no new live duel starts;
/// duels already running are untouched.
/// </summary>
[Alias("Quesshi.Grains.Abstractions.ILiveSettingsGrain")]
public interface ILiveSettingsGrain : IGrainWithIntegerKey
{
    [Alias("IsEnabledAsync")]
    Task<bool> IsEnabledAsync();

    [Alias("SetEnabledAsync")]
    Task SetEnabledAsync(bool enabled);

    /// <summary>
    /// Writes <paramref name="defaultValue"/> only if this grain has never persisted a value —
    /// called once at start-up so a restart seeds an install that has never heard of the setting
    /// without clobbering an operator's runtime toggle.
    /// </summary>
    [Alias("SeedAsync")]
    Task SeedAsync(bool defaultValue);
}
