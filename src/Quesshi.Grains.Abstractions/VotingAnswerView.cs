namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.VotingAnswerView")]
public sealed record VotingAnswerView(
    [property: Id(0)] int Kind,
    [property: Id(1)] string? ParticipantId,
    [property: Id(2)] int? ChoiceIndex,
    [property: Id(3)] DateTimeOffset At,
    /// <summary>The participant who submitted this answer. Set only on closed-slot answers.</summary>
    [property: Id(4)] string? PlayerId = null);
