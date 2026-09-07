namespace Quesshi.Grains.Abstractions;

/// <summary>
/// One round as a participant sees it. For the round still open, <c>CorrectIndex</c> is null and
/// so is every answer's <c>ChoiceIndex</c>/<c>Correct</c> except the asking player's own.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveRoundResultView")]
public sealed record LiveRoundResultView(
    [property: Id(0)] int Slot,
    [property: Id(1)] string QuestionId,
    [property: Id(2)] DateTimeOffset StartedAt,
    [property: Id(3)] int? CorrectIndex,
    [property: Id(4)] List<LiveRoundAnswerView> Answers);
