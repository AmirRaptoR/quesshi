namespace Quesshi.Grains.Abstractions;

/// <summary>One player's answer within a round, as redacted for the caller. <c>Answered</c> lets a
/// client draw "they have locked in" even while <c>ChoiceIndex</c>/<c>Correct</c> stay hidden.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveRoundAnswerView")]
public sealed record LiveRoundAnswerView(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] bool Answered,
    [property: Id(2)] int? ChoiceIndex,
    [property: Id(3)] bool? Correct,
    [property: Id(4)] int Score);
