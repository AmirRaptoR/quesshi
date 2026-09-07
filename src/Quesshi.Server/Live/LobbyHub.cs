using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;

namespace Quesshi.Server.Live;

/// <summary>
/// One connection per signed-in player, open for the whole session — not the single-duel lifetime
/// <c>/hub/live</c> has. Its job is presence — mark the caller online on connect, refresh it on
/// <see cref="Heartbeat"/>, and let the key expire on its own when the connection is gone — plus both
/// doors into a duel: the random queue (<see cref="QueueRandom"/>, <see cref="LeaveQueue"/>) and
/// friend challenges (<see cref="Challenge"/>, <see cref="Accept"/>, <see cref="Decline"/>, and
/// delivery of anything pending on <see cref="OnConnectedAsync"/>). Neither door decides a duel's
/// outcome; that is <c>ILiveMatchGrain</c>'s.
/// </summary>
[Authorize]
public sealed class LobbyHub(IGrainFactory grains, IPresence presence, ILobbyNotifier notifier,
    IPlayerRepository players, IIdFactory ids) : Hub
{
    /// <summary>Three heartbeats' worth, so two dropped beats don't flicker an online player offline.</summary>
    public static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(60);

    private ILiveLobbyGrain Lobby => grains.GetGrain<ILiveLobbyGrain>(0);

    public override async Task OnConnectedAsync()
    {
        // Unlike /hub/live, which #11 deliberately leaves open to a guest legitimately in a duel they
        // joined by code, nothing on this hub is ever open to a guest — there is no door here a guest
        // is meant to use, so the connection is refused outright rather than gated per-call.
        if (Context.User is null || Context.User.IsGuest())
        {
            Context.Abort();
            return;
        }

        var playerId = Context.User.PlayerId()!;
        await presence.MarkOnlineAsync(playerId, PresenceTtl);
        await Groups.AddToGroupAsync(Context.ConnectionId, playerId);

        // A challenge still within its 45 seconds survives a brief reconnect; one already gone is not
        // resurrected. PendingForAsync itself measures the remaining time from when it was sent.
        if (await Lobby.PendingForAsync(playerId) is { } challenge)
            await notifier.ChallengeReceivedAsync(playerId, ToNotice(challenge));

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Closing the tab dequeues you too: a queue entry cannot outlive the connection that
        // heartbeats it. A pending challenge is untouched — it still resolves via accept, decline or
        // its own timer, the same as it would across a brief reconnect.
        if (Context.User is { } user && !user.IsGuest())
        {
            var playerId = user.PlayerId()!;
            await presence.MarkOfflineAsync(playerId);
            await Lobby.LeaveAsync(playerId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Refreshes the caller's presence TTL and, if they are queued, their live queue entry too
    /// — the same connection heartbeats both, so an entry never lapses under a player who is still here.
    /// Called on a timer by <c>LobbyClient</c> while connected.</summary>
    public async Task Heartbeat()
    {
        var playerId = Context.User!.PlayerId()!;
        await presence.MarkOnlineAsync(playerId, PresenceTtl);
        await Lobby.HeartbeatAsync(playerId);
    }

    /// <summary>
    /// Queues the caller for a random live opponent. Returns the new match id if this call is the one
    /// that found a waiting opponent; null if this call is now the one waiting, or if the duel could
    /// not be built (in which case <see cref="ILobbyNotifier.QueueFailedAsync"/> is what tells both
    /// sides), or if the caller already holds a pending challenge. Refused explicitly for a guest, on
    /// top of the connection already being refused at <see cref="OnConnectedAsync"/> — provable
    /// without standing up a connection.
    /// </summary>
    public Task<string?> QueueRandom(int lang, int questionCount, List<string> categories, List<int> levels)
        => Context.User!.IsGuest()
            ? throw new HubException("guests cannot queue")
            : Lobby.EnqueueAsync(Context.User!.PlayerId()!, lang, questionCount, categories, levels);

    public Task LeaveQueue()
        => Context.User!.IsGuest()
            ? throw new HubException("guests cannot queue")
            : Lobby.LeaveAsync(Context.User!.PlayerId()!);

    /// <summary>
    /// Refuses a guest caller explicitly, on top of <see cref="OnConnectedAsync"/> refusing the
    /// connection itself — provable with no connection standing up at all.
    /// Returns a <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>.
    /// </summary>
    public async Task<int> Challenge(string targetId, string? lang, int questionCount, List<string> categoryIds, List<int> levels)
    {
        if (RequirePlayer() is not { } challengerId) return (int)LiveChallengeResult.NotFound;
        if (challengerId == targetId) return (int)LiveChallengeResult.SelfChallenge;

        var me = await players.GetAsync(challengerId);
        if (me is null || !me.Friends.Contains(targetId)) return (int)LiveChallengeResult.NotFound;
        if ((await presence.OnlineAsync([targetId])).Count == 0) return (int)LiveChallengeResult.TargetOffline;

        var resolvedLang = string.IsNullOrWhiteSpace(lang) ? me.Lang : lang.ToLanguage();
        return await Lobby.ChallengeAsync(ids.NewId(), challengerId, targetId, (int)resolvedLang, questionCount, categoryIds, levels);
    }

    public Task<LiveChallengeAcceptResult> Accept(string challengeId)
        => RequirePlayer() is { } me
            ? Lobby.AcceptAsync(challengeId, me)
            : Task.FromResult(new LiveChallengeAcceptResult((int)LiveChallengeResult.NotFound, null, null));

    public Task<int> Decline(string challengeId)
        => RequirePlayer() is { } me ? Lobby.DeclineAsync(challengeId, me) : Task.FromResult((int)LiveChallengeResult.NotFound);

    private string? RequirePlayer() => Context.User is null || Context.User.IsGuest() ? null : Context.User.PlayerId();

    private static LiveChallengeNotice ToNotice(LiveChallengeView c)
        => new(c.ChallengeId, c.ChallengerId, c.Lang, c.QuestionCount, c.CategoryIds, c.Levels, c.ExpiresAt);
}
