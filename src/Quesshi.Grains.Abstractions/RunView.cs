
namespace Quesshi.Grains.Abstractions;

/// <summary>One player's run as another player is allowed to see it. Choices are empty until the reveal.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.RunView")]
public sealed record RunView(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Score,
    [property: Id(2)] int Correct,
    [property: Id(3)] int Answered,
    [property: Id(4)] bool Finished,
    [property: Id(5)] List<int> Choices);
