namespace Quesshi.Shared;

/// <summary>
/// Redaction-safe SignalR notification that a voting slot crossed its answer barrier. Answers stay
/// empty while another slot is active and are included only when this closure ends the match.
/// </summary>
public sealed record VotingSlotClosedDto(int Slot, List<VotingAnswerPushDto> Answers);

public sealed record VotingAnswerPushDto(string PlayerId, int Kind, string? ParticipantId, int? ChoiceIndex);

/// <summary>The roster changed while a voting lobby or game was open.</summary>
public sealed record VotingRosterChangedDto;
