namespace Quesshi.Shared;

public sealed record VotingSlotDto(
    int Slot,
    string QuestionId,
    string Prompt,
    List<VotingOptionDto> Options,
    DateTimeOffset ServedAt,
    List<string> AnsweredParticipantIds,
    List<VotingAnswerDto> Answers,
    MediaDto? Media = null);
