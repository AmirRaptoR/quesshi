namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingSlotView")]
public sealed record MatchingSlotView(
    [property: Id(0)] int Slot,
    [property: Id(1)] string QuestionId,
    [property: Id(2)] string Prompt,
    [property: Id(3)] List<MatchingOptionView> Options,
    [property: Id(4)] DateTimeOffset ServedAt,
    [property: Id(5)] List<string> AnsweredParticipantIds,
    [property: Id(6)] List<MatchingAnswerView> Answers,
    [property: Id(7)] MatchingMediaView? Media = null);
