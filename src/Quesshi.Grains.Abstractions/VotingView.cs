namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingView")]
public sealed record VotingView(
    [property: Id(0)] string Id,
    [property: Id(1)] string Code,
    [property: Id(2)] int Lang,
    [property: Id(3)] int Capacity,
    [property: Id(4)] int State,
    [property: Id(5)] List<VotingParticipantView> Participants,
    [property: Id(6)] int? CurrentSlotIndex,
    [property: Id(7)] int TotalSlots,
    [property: Id(8)] VotingSlotView? CurrentSlot,
    [property: Id(9)] VotingSlotView? LastClosedSlot,
    [property: Id(10)] VotingAnswerView? OwnAnswer,
    [property: Id(11)] DateTimeOffset CreatedAt,
    [property: Id(12)] DateTimeOffset? EndedAt,
    [property: Id(13)] VotingResultsView? Results = null,
    [property: Id(14)] List<string>? CategoryIds = null,
    /// <summary>Every served slot in order, exposed only after the match ends.</summary>
    [property: Id(15)] List<VotingSlotView>? ClosedSlots = null);
