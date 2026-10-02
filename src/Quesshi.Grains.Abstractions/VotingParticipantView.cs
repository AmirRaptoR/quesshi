namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingParticipantView")]
public sealed record VotingParticipantView(
    [property: Id(0)] string Id,
    [property: Id(1)] bool Active);
