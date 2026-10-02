using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Infrastructure;

namespace Quesshi.Server.Hubs;

public sealed class SignalRVotingNotifier(IHubContext<LiveHub> hub, TenantContext tenant) : IVotingNotifier
{
    public Task SlotClosedAsync(string matchId, VotingSlotClosedPush push, CancellationToken ct = default)
        => Group(matchId).SendAsync("VotingSlotClosed", push, ct);

    public Task RosterChangedAsync(string matchId, CancellationToken ct = default)
        => Group(matchId).SendAsync("VotingRosterChanged", ct);

    private IClientProxy Group(string matchId) => hub.Clients.Group(LiveHub.GroupName(matchId, tenant.Id));
}
