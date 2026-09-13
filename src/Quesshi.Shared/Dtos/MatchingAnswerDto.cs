namespace Quesshi.Shared;

public sealed record MatchingAnswerDto(
    string Kind,
    string? ParticipantId,
    int? ChoiceIndex,
    DateTimeOffset At,
    /// <summary>The participant who submitted this answer, when it is part of a closed slot.</summary>
    string? PlayerId = null);
