using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Hubs;

/// <summary>
/// The production <see cref="ILiveNotifier"/> — <see cref="ILiveNotifier"/>'s own doc comment says
/// it "ships with no production implementation"; this is that implementation, backed by
/// <see cref="LiveHub"/>'s group per match id. Also the presence tracker <see cref="LiveHub"/>
/// drives: a connection maps to at most one (match, player) at a time, so a disconnect knows who to
/// announce as gone without the grain ever having to know a socket exists.
/// </summary>
public sealed class SignalRLiveNotifier(IHubContext<LiveHub> hub) : ILiveNotifier
{
    private readonly ConcurrentDictionary<string, (string MatchId, string PlayerId)> _connections = new();

    public Task CountdownStartedAsync(string matchId, LiveCountdown countdown, CancellationToken ct = default)
        => Task.CompletedTask; // no client listens for this push (see #13's contract-additions note); the phase is read from the catch-up view instead.

    public Task RoundStartedAsync(string matchId, LiveRoundCard card, CancellationToken ct = default)
        => Group(matchId).SendAsync("RoundStarted", card.ToDto(), ct);

    public Task RoundRevealedAsync(string matchId, LiveRoundReveal reveal, CancellationToken ct = default)
        => Group(matchId).SendAsync("RoundRevealed", reveal.ToDto(), ct);

    public Task EndedAsync(string matchId, LiveEnded ended, CancellationToken ct = default)
        => Group(matchId).SendAsync("Ended", ended.ToDto(), ct);

    public Task OpponentPresenceChangedAsync(string matchId, string playerId, bool online, CancellationToken ct = default)
        => Group(matchId).SendAsync(online ? "OpponentBack" : "OpponentLeft", new OpponentPresenceDto(playerId, online), ct);

    public Task OpponentAnsweredAsync(string matchId, int slot, string playerId, CancellationToken ct = default)
        => Group(matchId).SendAsync("OpponentAnswered", new OpponentAnsweredDto(matchId, slot, playerId), ct);

    public Task RematchRequestedAsync(string matchId, string playerId, CancellationToken ct = default)
        => Group(matchId).SendAsync("RematchRequested", new RematchRequestedDto(playerId), ct);

    public Task RematchCreatedAsync(string matchId, string newMatchId, CancellationToken ct = default)
        => Group(matchId).SendAsync("RematchCreated", new RematchCreatedDto(newMatchId), ct);

    public Task RematchFailedAsync(string matchId, CancellationToken ct = default)
        => Group(matchId).SendAsync("RematchFailed", ct);

    /// <summary>Called from <see cref="LiveHub.Join"/> on every connect and reconnect alike — a duplicate "back" for a
    /// connection that was never away is harmless, and simpler than tracking whether it was.</summary>
    public Task NoteConnectedAsync(string connectionId, string matchId, string playerId)
    {
        _connections[connectionId] = (matchId, playerId);
        return OpponentPresenceChangedAsync(matchId, playerId, online: true);
    }

    public Task NoteDisconnectedAsync(string connectionId)
        => _connections.TryRemove(connectionId, out var info)
            ? OpponentPresenceChangedAsync(info.MatchId, info.PlayerId, online: false)
            : Task.CompletedTask;

    private IClientProxy Group(string matchId) => hub.Clients.Group(LiveHub.GroupName(matchId));
}
