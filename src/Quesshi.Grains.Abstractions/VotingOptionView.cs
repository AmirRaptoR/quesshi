namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingOptionView")]
public sealed record VotingOptionView(
    [property: Id(0)] int Kind,
    [property: Id(1)] string? ParticipantId,
    [property: Id(2)] int? ChoiceIndex,
    [property: Id(3)] string? Text);
