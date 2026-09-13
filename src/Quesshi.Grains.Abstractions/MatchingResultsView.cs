namespace Quesshi.Grains.Abstractions;

/// <summary>Grain-wire form of one closed slot's served-option distribution.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingSlotResultView")]
public sealed record MatchingSlotResultView(
    [property: Id(0)] int Slot,
    [property: Id(1)] List<int> Counts,
    [property: Id(2)] bool AllAgreed);

/// <summary>Grain-wire form of one deterministic participant pair's whole-match statistics.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingPairStatView")]
public sealed record MatchingPairStatView(
    [property: Id(0)] string FirstParticipantId,
    [property: Id(1)] string SecondParticipantId,
    [property: Id(2)] int Same,
    [property: Id(3)] int Different,
    [property: Id(4)] int? AgreementPercent);

/// <summary>
/// Grain-wire matching results. Null slot entries are still present to preserve served slot order;
/// they represent barriers that have not closed. Pairwise results remain null until completion.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingResultsView")]
public sealed record MatchingResultsView(
    [property: Id(0)] List<MatchingSlotResultView?> Slots,
    [property: Id(1)] List<MatchingPairStatView>? PairStats,
    [property: Id(2)] int? AllAgreedCount);
