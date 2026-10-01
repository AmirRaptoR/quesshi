using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Infrastructure;

namespace Quesshi.Server.Hubs;

public sealed class SignalRMatchingNotifier(IHubContext<LiveHub> hub, TenantContext tenant) : IMatchingNotifier
{
    public Task SlotClosedAsync(string matchId, MatchingSlotClosedPush push, CancellationToken ct = default)
        => Group(matchId).SendAsync("MatchingSlotClosed", push, ct);

    public Task RosterChangedAsync(string matchId, CancellationToken ct = default)
        => Group(matchId).SendAsync("MatchingRosterChanged", ct);

    private IClientProxy Group(string matchId) => hub.Clients.Group(LiveHub.GroupName(matchId, tenant.Id));
}
