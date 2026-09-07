using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Server.Api;

namespace Quesshi.Server.Live;

/// <summary>
/// One connection per signed-in player, open for the whole session — not the single-duel lifetime
/// <c>/hub/live</c> (still unbuilt) will have. Its only job is presence: mark the caller online on
/// connect, refresh it on <see cref="Heartbeat"/>, and let the key expire on its own when the
/// connection is gone. Nothing here decides a duel's outcome.
/// </summary>
[Authorize]
public sealed class LobbyHub(IPresence presence) : Hub
{
    /// <summary>Three heartbeats' worth, so two dropped beats don't flicker an online player offline.</summary>
    public static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(60);

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
        if (!Context.User!.IsGuest())
            await presence.MarkOfflineAsync(Context.User!.PlayerId()!);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Refreshes the caller's presence TTL. Called on a timer by <c>LobbyClient</c> while connected.</summary>
    public Task Heartbeat() => presence.MarkOnlineAsync(Context.User!.PlayerId()!, PresenceTtl);
}
