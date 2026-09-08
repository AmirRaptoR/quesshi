namespace Quesshi.Grains.Abstractions;

/// <summary>
/// Everything a target needs to render an invitation banner, and everything accepting needs to join
/// the lobby it points at. Carries no settings — <see cref="LobbyId"/> already identifies the lobby
/// that owns them (see <c>ILiveMatchmakingGrain.ChallengeAsync</c>'s own remarks for why this changed
/// from the settings-carrying shape it used to have).
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveChallengeView")]
public sealed record LiveChallengeView(
    [property: Id(0)] string ChallengeId,
    [property: Id(1)] string ChallengerId,
    [property: Id(2)] string TargetId,
    [property: Id(3)] string LobbyId,
    [property: Id(4)] string LobbyCode,
    [property: Id(5)] DateTimeOffset SentAt,
    [property: Id(6)] DateTimeOffset ExpiresAt);
