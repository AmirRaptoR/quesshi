using Orleans;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Api;

/// <summary>Seats players through the session grain's join, which already answers "joined" for a player who holds a seat.</summary>
public sealed class GrainSessionSeating(IGrainFactory grains) : ISessionSeating
{
    public async Task<bool> SeatAsync(
        Guid tenantId, Guid sessionId, Guid playerId, Guid pairingToken, CancellationToken cancellationToken = default)
    {
        var result = await grains.GetGrain<ISessionGrain>($"{tenantId}/{sessionId}")
            .ExecuteLobbyAsync(new LobbyCommand(playerId.ToString(), "join"))
            .WaitAsync(cancellationToken);
        return result.Code switch
        {
            "joined" => true,
            "stale_activation" => throw new InvalidOperationException("The session grain's state was stale; the join may not have happened."),
            _ => false,
        };
    }
}
