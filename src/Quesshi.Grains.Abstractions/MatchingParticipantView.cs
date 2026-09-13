namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingParticipantView")]
public sealed record MatchingParticipantView(
    [property: Id(0)] string Id,
    [property: Id(1)] bool Active);
