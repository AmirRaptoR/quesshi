namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingOptionView")]
public sealed record MatchingOptionView(
    [property: Id(0)] int Kind,
    [property: Id(1)] string? ParticipantId,
    [property: Id(2)] int? ChoiceIndex,
    [property: Id(3)] string? Text);
