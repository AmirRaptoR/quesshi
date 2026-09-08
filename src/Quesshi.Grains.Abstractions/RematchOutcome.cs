namespace Quesshi.Grains.Abstractions;

/// <summary>
/// A <c>RematchStatus</c> carried as <c>int</c> for the same reason <see cref="LiveView"/>'s own
/// <c>State</c>/<c>Phase</c> fields are: <see cref="Quesshi.Grains.Abstractions"/> keeps its
/// Orleans-SDK-only reference list, so the domain enum crosses the boundary as a number. Only
/// <c>Status == Created</c> ever carries a <see cref="NewMatchId"/>/<see cref="NewMatchCode"/> — the
/// rematch lobby's id and share code, derived from the finished match's own (see
/// <c>ILiveMatchGrain.RequestRematchAsync</c>), not freshly minted, which is what makes it safe for
/// more than one participant to call this and land on the same value. <see cref="NewMatchCode"/> is
/// what a guest participant is invited by: they never receive the in-app invitation every other
/// participant gets (see <c>LobbyHub.OnConnectedAsync</c>'s own remarks), so the code handed back
/// here — and pushed to everyone else via <c>ILiveNotifier.RematchCreatedAsync</c> — is the entire
/// invitation reach a guest ever has.
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.RematchOutcome")]
public sealed record RematchOutcome(
    [property: Id(0)] int Status,
    [property: Id(1)] string? NewMatchId = null,
    [property: Id(2)] string? NewMatchCode = null);
