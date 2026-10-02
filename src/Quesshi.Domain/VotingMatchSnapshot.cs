namespace Quesshi.Domain;

/// <summary>
/// Complete persisted state for a <see cref="VotingMatch"/>. The slot list contains the served
/// prompt/options snapshots and their raw answers; no endpoint-facing view is built from this type.
/// </summary>
public sealed record VotingMatchSnapshot(
    string Id,
    string Code,
    List<string> Participants,
    int Capacity,
    List<VotingQuestionSnapshot> Questions,
    List<VotingSlotSnapshot> Slots,
    int? CurrentSlot,
    HashSet<string> InactiveParticipants,
    MatchState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt)
{
    /// <summary>Compatibility/readability alias for consumers that describe the persisted roster by
    /// who remains active rather than who has departed.</summary>
    public IReadOnlyList<string> ActiveParticipants =>
        Participants.Where(participantId => !InactiveParticipants.Contains(participantId)).ToArray();

    public int? CurrentSlotIndex => CurrentSlot;
}
