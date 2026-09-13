namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingView")]
public sealed record MatchingView(
    [property: Id(0)] string Id,
    [property: Id(1)] string Code,
    [property: Id(2)] int Lang,
    [property: Id(3)] int Capacity,
    [property: Id(4)] int State,
    [property: Id(5)] List<MatchingParticipantView> Participants,
    [property: Id(6)] int? CurrentSlotIndex,
    [property: Id(7)] int TotalSlots,
    [property: Id(8)] MatchingSlotView? CurrentSlot,
    [property: Id(9)] MatchingSlotView? LastClosedSlot,
    [property: Id(10)] MatchingAnswerView? OwnAnswer,
    [property: Id(11)] DateTimeOffset CreatedAt,
    [property: Id(12)] DateTimeOffset? EndedAt);
