namespace Quesshi.Shared;

public sealed record SubmitVotingAnswerDto(
    int Slot,
    string Kind,
    string? ParticipantId = null,
    int? ChoiceIndex = null);
