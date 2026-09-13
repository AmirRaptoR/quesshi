namespace Quesshi.Shared;

public sealed record SubmitMatchingAnswerDto(
    int Slot,
    string Kind,
    string? ParticipantId = null,
    int? ChoiceIndex = null);
