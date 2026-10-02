namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingMediaView")]
public sealed record VotingMediaView(
    [property: Id(0)] int Kind,
    [property: Id(1)] string Url,
    [property: Id(2)] string? Attribution = null);
