namespace Quesshi.Shared;

/// <summary>
/// Redaction-safe SignalR notification that a matching slot crossed its answer barrier. Answers stay
/// empty while another slot is active and are included only when this closure ends the match.
/// </summary>
public sealed record MatchingSlotClosedDto(int Slot, List<MatchingAnswerPushDto> Answers);

public sealed record MatchingAnswerPushDto(string PlayerId, int Kind, string? ParticipantId, int? ChoiceIndex);

/// <summary>The roster changed while a matching lobby or game was open.</summary>
public sealed record MatchingRosterChangedDto;
