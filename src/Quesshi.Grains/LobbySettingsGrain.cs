using Orleans.Runtime;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Grains;

public sealed class LobbySettingsGrain(
    [PersistentState("lobby-settings", "hot")] IPersistentState<LobbySettingsState> state)
    : Grain, ILobbySettingsGrain
{
    public Task<int> GetMaxCapacityAsync() => Task.FromResult(state.State.MaxCapacity ?? MatchRules.DefaultMaxParticipants);

    public async Task<bool> SetMaxCapacityAsync(int value)
    {
        if (value is < MatchRules.MinParticipants or > MatchRules.MaxParticipants) return false;
        state.State.MaxCapacity = value;
        await state.WriteStateAsync();
        return true;
    }

    public async Task SeedAsync(int defaultValue)
    {
        if (state.State.MaxCapacity is not null) return;
        state.State.MaxCapacity = defaultValue is >= MatchRules.MinParticipants and <= MatchRules.MaxParticipants
            ? defaultValue : MatchRules.DefaultMaxParticipants;
        await state.WriteStateAsync();
    }
}
