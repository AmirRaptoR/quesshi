namespace Quesshi.Grains.Abstractions;

/// <summary>
/// One participant's place in a finished duel. Mirrors the domain's <c>Standing</c>, which this
/// project cannot name: it keeps its Orleans-SDK-only reference list, so the outcome crosses as an
/// <c>int</c> here exactly as <c>LiveJoinResult</c> does on <c>ILiveMatchGrain.JoinAsync</c>.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveStandingView")]
public sealed record LiveStandingView(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] int Score,
    [property: Id(2)] int Place,
    [property: Id(3)] int Outcome);
