using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;

namespace Quesshi.Server.Live;

/// <summary>
/// One connection per signed-in player, open for the whole session — not the single-duel lifetime
/// <c>/hub/live</c> (still unbuilt) will have. Presence is its original job: mark the caller online on
/// connect, refresh it on <see cref="Heartbeat"/>, and let the key expire on its own when the
/// connection is gone. The random live queue rides the same connection for the same reason: an entry
/// cannot outlive the socket that keeps it alive.
/// </summary>
[Authorize]
public sealed class LobbyHub(IPresence presence, IGrainFactory grains) : Hub
{
    /// <summary>Three heartbeats' worth, so two dropped beats don't flicker an online player offline.</summary>
    public static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(60);

    private ILiveLobbyGrain Queue => grains.GetGrain<ILiveLobbyGrain>(0);

    public override async Task OnConnectedAsync()
    {
        // Unlike /hub/live, which #11 deliberately leaves open to a guest legitimately in a duel they
        // joined by code, nothing on this hub is ever open to a guest — there is no door here a guest
        // is meant to use, so the connection is refused outright rather than gated per-call.
        if (Context.User!.IsGuest())
        {
            Context.Abort();
            return;
        }

        await presence.MarkOnlineAsync(Context.User!.PlayerId()!, PresenceTtl);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Closing the tab dequeues you: a queue entry cannot outlive the connection that heartbeats it.
        if (!Context.User!.IsGuest())
        {
            await presence.MarkOfflineAsync(Context.User!.PlayerId()!);
            await Queue.LeaveAsync(Context.User!.PlayerId()!);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Refreshes the caller's presence TTL. Called on a timer by <c>LobbyClient</c> while connected.</summary>
    public Task Heartbeat() => presence.MarkOnlineAsync(Context.User!.PlayerId()!, PresenceTtl);

    /// <summary>
    /// Queues the caller for a random live opponent. Returns the new match id if this call is the one
    /// that found a waiting opponent; null if this call is now the one waiting, or if the duel could
    /// not be built (in which case <see cref="ILobbyNotifier.QueueFailedAsync"/> is what tells both
    /// sides). Refused explicitly for a guest, on top of the connection already being refused at
    /// <see cref="OnConnectedAsync"/> — provable without standing up a connection.
    /// </summary>
    public Task<string?> QueueRandom(int lang, int questionCount, List<string> categories, List<int> levels)
        => Context.User!.IsGuest()
            ? throw new HubException("guests cannot queue")
            : Queue.EnqueueAsync(Context.User!.PlayerId()!, lang, questionCount, categories, levels);

    public Task LeaveQueue()
        => Context.User!.IsGuest()
            ? throw new HubException("guests cannot queue")
            : Queue.LeaveAsync(Context.User!.PlayerId()!);
}
