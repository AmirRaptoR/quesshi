using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;

namespace Quesshi.Server.Hubs;

public sealed class SignalRMatchingNotifier(IHubContext<LiveHub> hub) : IMatchingNotifier
{
    public Task SlotClosedAsync(string matchId, MatchingSlotClosedPush push, CancellationToken ct = default)
        => Group(matchId).SendAsync("MatchingSlotClosed", push, ct);

    public Task RosterChangedAsync(string matchId, CancellationToken ct = default)
        => Group(matchId).SendAsync("MatchingRosterChanged", ct);

    private IClientProxy Group(string matchId) => hub.Clients.Group($"live:{matchId}");
}
