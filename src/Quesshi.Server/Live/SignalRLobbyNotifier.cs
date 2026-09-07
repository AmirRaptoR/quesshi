using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;

namespace Quesshi.Server.Live;

/// <summary>Pushes the three ILobbyNotifier events to a player's /hub/lobby connection, by player id
/// — SignalR's default IUserIdProvider reads the same NameIdentifier claim LobbyHub's Context.User does.</summary>
public sealed class SignalRLobbyNotifier(IHubContext<LobbyHub> hub) : ILobbyNotifier
{
    public Task MatchedAsync(string playerId, string matchId, CancellationToken ct = default)
        => hub.Clients.User(playerId).SendAsync("Matched", matchId, ct);

    public Task QueueCountChangedAsync(string playerId, int othersWaiting, CancellationToken ct = default)
        => hub.Clients.User(playerId).SendAsync("QueueCount", othersWaiting, ct);

    public Task QueueFailedAsync(string playerId, CancellationToken ct = default)
        => hub.Clients.User(playerId).SendAsync("QueueFailed", ct);
}
