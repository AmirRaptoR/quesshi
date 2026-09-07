namespace Quesshi.Grains.Abstractions;

/// <summary>
/// A <c>RematchStatus</c> carried as <c>int</c> for the same reason <see cref="LiveView"/>'s own
/// <c>State</c>/<c>Phase</c> fields are: <see cref="Quesshi.Grains.Abstractions"/> keeps its
/// Orleans-SDK-only reference list, so the domain enum crosses the boundary as a number. Only
/// <c>Status == Created</c> ever carries a <see cref="NewMatchId"/>.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.RematchOutcome")]
public sealed record RematchOutcome(
    [property: Id(0)] int Status,
    [property: Id(1)] string? NewMatchId = null);
