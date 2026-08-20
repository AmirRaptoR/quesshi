namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.PlayerCard")]
public sealed record PlayerCard(
    [property: Id(0)] string Id,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] string AvatarSeed);
