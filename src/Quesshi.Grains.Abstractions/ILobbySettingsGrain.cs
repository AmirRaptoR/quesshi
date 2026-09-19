namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.ILobbySettingsGrain")]
public interface ILobbySettingsGrain : IGrainWithIntegerKey
{
    [Alias("GetMaxCapacityAsync")] Task<int> GetMaxCapacityAsync();
    [Alias("SetMaxCapacityAsync")] Task<bool> SetMaxCapacityAsync(int value);
    [Alias("SeedAsync")] Task SeedAsync(int defaultValue);
}
