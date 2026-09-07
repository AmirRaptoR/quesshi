using Orleans;
using Orleans.Runtime;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

/// <summary>The live-duel kill switch. See <see cref="ILiveSettingsGrain"/>.</summary>
public sealed class LiveSettingsGrain(
    [PersistentState("live-settings", "hot")] IPersistentState<LiveSettingsState> state)
    : Grain, ILiveSettingsGrain
{
    // No configuration has ever reached this grain before the first SeedAsync, so an install
    // whose Program.cs has not run yet still answers "enabled" — matches the criterion that
    // Live:Enabled defaults to true when absent from configuration.
    public Task<bool> IsEnabledAsync() => Task.FromResult(state.State.Enabled ?? true);

    public async Task SetEnabledAsync(bool enabled)
    {
        state.State.Enabled = enabled;
        await state.WriteStateAsync();
    }

    public async Task SeedAsync(bool defaultValue)
    {
        if (state.State.Enabled is not null) return; // a runtime toggle already lives here; a restart must not overwrite it
        state.State.Enabled = defaultValue;
        await state.WriteStateAsync();
    }
}
