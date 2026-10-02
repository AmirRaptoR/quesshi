namespace Quesshi.Grains.Abstractions;

/// <summary>Grain-wire form of one closed slot's served-option distribution.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingSlotResultView")]
public sealed record VotingSlotResultView(
    [property: Id(0)] int Slot,
    [property: Id(1)] List<int> Counts,
    [property: Id(2)] bool AllAgreed);

/// <summary>Grain-wire form of one deterministic participant pair's whole-match statistics.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingPairStatView")]
public sealed record VotingPairStatView(
    [property: Id(0)] string FirstParticipantId,
    [property: Id(1)] string SecondParticipantId,
    [property: Id(2)] int Same,
    [property: Id(3)] int Different,
    [property: Id(4)] int? AgreementPercent);

/// <summary>
/// Grain-wire voting results. Null slot entries are still present to preserve served slot order;
/// they represent barriers that have not closed. Pairwise results remain null until completion.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingResultsView")]
public sealed record VotingResultsView(
    [property: Id(0)] List<VotingSlotResultView?> Slots,
    [property: Id(1)] List<VotingPairStatView>? PairStats,
    [property: Id(2)] int? AllAgreedCount);
