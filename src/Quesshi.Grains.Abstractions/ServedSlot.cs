namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.ServedSlot")]
public sealed record ServedSlot(
    [property: Id(0)] int Slot,
    [property: Id(1)] string QuestionId,
    [property: Id(2)] int SecondsLimit,
    /// <summary>Questions in this match — the length is a per-match choice now, not a constant.</summary>
    [property: Id(3)] int Total);
