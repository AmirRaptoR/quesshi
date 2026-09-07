using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;

namespace Quesshi.Server.Live;

/// <summary>Pushes to whichever connections a player currently holds to <c>/hub/lobby</c> — every
/// connection is added to a group named after the player id on connect.</summary>
public sealed class SignalRLobbyNotifier(IHubContext<LobbyHub> hub) : ILobbyNotifier
{
    public Task ChallengeReceivedAsync(string targetId, LiveChallengeNotice challenge, CancellationToken ct = default)
        => hub.Clients.Group(targetId).SendAsync("ChallengeReceived", challenge, ct);

    public Task ChallengeExpiredAsync(string challengerId, string challengeId, CancellationToken ct = default)
        => hub.Clients.Group(challengerId).SendAsync("ChallengeExpired", challengeId, ct);

    public Task ChallengeDeclinedAsync(string challengerId, string challengeId, CancellationToken ct = default)
        => hub.Clients.Group(challengerId).SendAsync("ChallengeDeclined", challengeId, ct);

    public Task DuelReadyAsync(string playerId, string matchId, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("DuelReady", matchId, ct);

    public Task ChallengeFailedAsync(string playerId, string challengeId, CancellationToken ct = default)
        => hub.Clients.Group(playerId).SendAsync("ChallengeFailed", challengeId, ct);
}
