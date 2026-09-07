namespace Quesshi.Shared;

/// <summary>
/// One round as a participant sees it. For the round still open, <c>CorrectIndex</c> is null and so
/// is every answer's <c>ChoiceIndex</c>/<c>Correct</c> except the asking player's own.
/// </summary>
public sealed record LiveRoundResultViewDto(int Slot, string QuestionId, DateTimeOffset StartedAt, int? CorrectIndex, List<LiveRoundAnswerViewDto> Answers);
