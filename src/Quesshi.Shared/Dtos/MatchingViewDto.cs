namespace Quesshi.Shared;

public sealed record MatchingViewDto(
    string Id,
    string Code,
    string Mode,
    string Lang,
    int Capacity,
    string State,
    List<MatchingParticipantDto> Participants,
    int? CurrentSlotIndex,
    int TotalSlots,
    MatchingSlotDto? CurrentSlot,
    MatchingSlotDto? LastClosedSlot,
    MatchingAnswerDto? OwnAnswer,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    MatchingResultsDto? Results = null,
    List<string>? CategoryIds = null,
    List<MatchingSlotDto>? ClosedSlots = null);
