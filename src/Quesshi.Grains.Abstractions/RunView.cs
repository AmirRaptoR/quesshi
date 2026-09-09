namespace Quesshi.Grains.Abstractions;

/// <summary>One player's run as another player is allowed to see it. Choices are empty until the reveal.
/// <para>
/// <see cref="Responses"/> is the same list for the answers a choice index cannot hold — a sorting
/// order in stored-index terms, or a map answer — and is redacted on exactly the same terms and at
/// exactly the same moment as <see cref="Choices"/>, because a sort answer left visible early would
/// leak the same thing a choice index does. It is positional against <see cref="Choices"/>: entry
/// <c>n</c> of each describes the same answered slot, and an entry is null wherever that slot's
/// answer was an ordinary choice.
/// </para></summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.RunView")]
public sealed record RunView(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Score,
    [property: Id(2)] int Correct,
    [property: Id(3)] int Answered,
    [property: Id(4)] bool Finished,
    [property: Id(5)] List<int> Choices,
    [property: Id(6)] List<string?>? Responses = null);
