namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.AnswerOutcome")]
public sealed record AnswerOutcome(
    [property: Id(0)] bool Correct,
    [property: Id(1)] int CorrectIndex,
    [property: Id(2)] int Score,
    [property: Id(3)] string? Explanation,
    [property: Id(4)] bool RunFinished,
    [property: Id(5)] int RunScore);
