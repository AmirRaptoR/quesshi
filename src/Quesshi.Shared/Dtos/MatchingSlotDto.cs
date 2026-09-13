namespace Quesshi.Shared;

public sealed record MatchingSlotDto(
    int Slot,
    string QuestionId,
    string Prompt,
    List<MatchingOptionDto> Options,
    DateTimeOffset ServedAt,
    List<string> AnsweredParticipantIds,
    List<MatchingAnswerDto> Answers,
    MediaDto? Media = null);
