namespace Quesshi.Shared;

public sealed record MatchingAnswerDto(
    string Kind,
    string? ParticipantId,
    int? ChoiceIndex,
    DateTimeOffset At);
