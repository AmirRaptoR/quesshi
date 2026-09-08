namespace Quesshi.Grains.Abstractions;

/// <summary><c>Enabled</c> is null exactly when nothing has ever been persisted — the state
/// <see cref="ILiveSettingsGrain.SeedAsync"/> checks for before writing a configured default,
/// so a value of <c>false</c> is distinguishable from "never set".</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveSettingsState")]
public sealed class LiveSettingsState
{
    [Id(0)] public bool? Enabled { get; set; }
}
