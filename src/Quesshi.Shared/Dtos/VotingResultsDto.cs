using System.Text.Json.Serialization;

namespace Quesshi.Shared;

/// <summary>One closed voting slot's option distribution in the order it was served.</summary>
public sealed record VotingSlotResultDto(int Slot, List<int> Counts, bool AllAgreed)
{
    [JsonIgnore]
    public List<int> OptionCounts => Counts;
}

/// <summary>Whole-match agreement between an unordered pair of participants.</summary>
public sealed record VotingPairStatDto(string FirstParticipantId, string SecondParticipantId, int Same,
    int Different, int? AgreementPercent)
{
    [JsonIgnore]
    public string ParticipantA => FirstParticipantId;

    [JsonIgnore]
    public string ParticipantB => SecondParticipantId;
}

/// <summary>
/// Voting statistics, exposed only after the match ends. A null slot entry means that a no-contest
/// match ended while that slot's answer barrier was still open.
/// </summary>
public sealed record VotingResultsDto(List<VotingSlotResultDto?> Slots,
    List<VotingPairStatDto>? PairStats, int? AllAgreedCount)
{
    [JsonIgnore]
    public List<VotingPairStatDto>? Pairs => PairStats;
}
