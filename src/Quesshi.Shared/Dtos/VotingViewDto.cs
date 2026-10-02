namespace Quesshi.Shared;

public sealed record VotingViewDto(
    string Id,
    string Code,
    string Mode,
    string Lang,
    int Capacity,
    string State,
    List<VotingParticipantDto> Participants,
    int? CurrentSlotIndex,
    int TotalSlots,
    VotingSlotDto? CurrentSlot,
    VotingSlotDto? LastClosedSlot,
    VotingAnswerDto? OwnAnswer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    VotingResultsDto? Results = null,
    List<string>? CategoryIds = null,
    List<VotingSlotDto>? ClosedSlots = null);
