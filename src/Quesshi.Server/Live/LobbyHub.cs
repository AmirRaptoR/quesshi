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
/// <c>/hub/live</c> (still unbuilt) will have. Its job is presence — mark the caller online on
/// connect, refresh it on <see cref="Heartbeat"/>, and let the key expire on its own when the
/// connection is gone — plus friend challenges: <see cref="Challenge"/>, <see cref="Accept"/>,
/// <see cref="Decline"/>, and delivery of anything pending on <see cref="OnConnectedAsync"/>.
/// Nothing here decides a duel's outcome.
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
        if (Context.User is { } user && !user.IsGuest())
            await presence.MarkOfflineAsync(user.PlayerId()!);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Refreshes the caller's presence TTL. Called on a timer by <c>LobbyClient</c> while connected.</summary>
    public Task Heartbeat() => presence.MarkOnlineAsync(Context.User!.PlayerId()!, PresenceTtl);

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
