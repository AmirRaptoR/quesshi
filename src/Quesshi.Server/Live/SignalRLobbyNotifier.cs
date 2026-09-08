using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Shared;

namespace Quesshi.Server.Live;

/// <summary>Pushes to whichever connections a player currently holds to <c>/hub/lobby</c> — every
/// connection is added to a group named after the player id on connect.</summary>
public sealed class SignalRLobbyNotifier(IHubContext<LobbyHub> hub, IPlayerRepository players) : ILobbyNotifier
{
    public async Task ChallengeReceivedAsync(string targetId, LiveChallengeNotice challenge, CancellationToken ct = default)
    {
        var challenger = await players.GetAsync(challenge.ChallengerId, ct);
        var dto = new LiveChallengeDto(challenge.ChallengeId, challenge.ChallengerId,
            challenger?.DisplayName ?? "—", challenger?.AvatarSeed ?? challenge.ChallengerId,
            challenge.LobbyId, challenge.LobbyCode, challenge.ExpiresAt);

        await hub.Clients.Group(targetId).SendAsync("ChallengeReceived", dto, ct);
    }

    public Task ChallengeExpiredAsync(string challengerId, string challengeId, CancellationToken ct = default)
        => hub.Clients.Group(challengerId).SendAsync("ChallengeExpired", challengeId, ct);

    public Task ChallengeDeclinedAsync(string challengerId, string challengeId, CancellationToken ct = default)
        => hub.Clients.Group(challengerId).SendAsync("ChallengeDeclined", challengeId, ct);

    public Task DuelReadyAsync(string playerId, string matchId, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("DuelReady", matchId, ct);

    public Task ChallengeFailedAsync(string playerId, string challengeId, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("ChallengeFailed", challengeId, ct);

    public Task MatchedAsync(string playerId, string matchId, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("Matched", matchId, ct);

    public Task QueueCountChangedAsync(string playerId, int othersWaiting, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("QueueCount", othersWaiting, ct);

    public Task QueueFailedAsync(string playerId, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("QueueFailed", ct);
}
