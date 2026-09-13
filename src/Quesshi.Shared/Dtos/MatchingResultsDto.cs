using System.Text.Json.Serialization;

namespace Quesshi.Shared;

/// <summary>One closed matching slot's option distribution in the order it was served.</summary>
public sealed record MatchingSlotResultDto(int Slot, List<int> Counts, bool AllAgreed)
{
    [JsonIgnore]
    public List<int> OptionCounts => Counts;
}

/// <summary>Whole-match agreement between an unordered pair of participants.</summary>
public sealed record MatchingPairStatDto(string FirstParticipantId, string SecondParticipantId, int Same,
    int Different, int? AgreementPercent)
{
    [JsonIgnore]
    public string ParticipantA => FirstParticipantId;

    [JsonIgnore]
    public string ParticipantB => SecondParticipantId;
}

/// <summary>
/// Matching statistics. A null slot entry means that slot's answer barrier is still open. The
/// pairwise list and group count are null until the whole match is complete; no-contest matches
/// therefore expose only the closed per-slot results.
/// </summary>
public sealed record MatchingResultsDto(List<MatchingSlotResultDto?> Slots,
    List<MatchingPairStatDto>? PairStats, int? AllAgreedCount)
{
    [JsonIgnore]
    public List<MatchingPairStatDto>? Pairs => PairStats;
}
