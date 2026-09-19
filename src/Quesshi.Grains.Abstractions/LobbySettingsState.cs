namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LobbySettingsState")]
public sealed class LobbySettingsState
{
    [Id(0)] public int? MaxCapacity { get; set; }
}
