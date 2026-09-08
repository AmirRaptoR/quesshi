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
    IPlayerRepository players, IIdFactory ids, IMatchArchive archive) : Hub
{
    /// <summary>Three heartbeats' worth, so two dropped beats don't flicker an online player offline.</summary>
    public static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many of the challenger's most recent duels (either kind) <see cref="WerePastOpponentsAsync"/>
    /// looks through for the target. "Former co-participant" is deliberately scoped to recent history,
    /// not the whole archive: the relaxation exists for "you just played this stranger", not "you were
    /// ever, at any point, in the same duel as this account" — the latter would make the friendship
    /// gate meaningless for anyone with enough matches behind them.
    /// </summary>
    private const int CoParticipantLookback = 20;

    private ILiveMatchmakingGrain Matchmaking => grains.GetGrain<ILiveMatchmakingGrain>(0);

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

        // Every invitation still within its lobby's own lifetime survives a brief reconnect — no
        // longer a fixed 45 seconds, and no longer just one: with exclusivity gone (see
        // ILiveMatchmakingGrain's own remarks) a player can hold several pending invitations at once,
        // so every one PendingForAsync returns is delivered, not just the first.
        foreach (var challenge in await Matchmaking.PendingForAsync(playerId))
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
            await Matchmaking.LeaveAsync(playerId);
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
        await Matchmaking.HeartbeatAsync(playerId);
    }

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
            : Matchmaking.EnqueueAsync(Context.User!.PlayerId()!, lang, questionCount, categories, levels);

    public Task LeaveQueue()
        => Context.User!.IsGuest()
            ? throw new HubException("guests cannot queue")
            : Matchmaking.LeaveAsync(Context.User!.PlayerId()!);

    /// <summary>
    /// Refuses a guest caller explicitly, on top of <see cref="OnConnectedAsync"/> refusing the
    /// connection itself — provable with no connection standing up at all. Opens a fresh capacity-2
    /// lobby owned by the caller with these settings, then invites the target to it — a challenge
    /// itself carries no settings any more (see <see cref="ILiveMatchmakingGrain.ChallengeAsync"/>'s
    /// own remarks), so whoever creates the lobby it points at is what fixes them, exactly as picking
    /// them on <c>Home.razor</c> before creating a duel always has. Sent regardless of whether the
    /// target is online right now: an invitation outlives the recipient's connection and is delivered
    /// on their next connect (see <see cref="OnConnectedAsync"/>), which is the entire point of it no
    /// longer expiring in 45 seconds. Returns a <c>Quesshi.Domain.LiveChallengeResult</c> as <c>int</c>.
    ///
    /// The friendship gate has one narrow, explicit relaxation: a target who is not (yet) a friend but
    /// who demonstrably just played the challenger in some other duel — a former co-participant — may
    /// still be challenged. This exists for the random opponent you have just finished playing, who a
    /// strict friendship check would make unreachable in-app the moment the duel ends, even though the
    /// two of you were, a moment ago, sitting in the very same lobby. Anyone else — a stranger with no
    /// shared match at all — still needs the friendship; see <see cref="WerePastOpponentsAsync"/>.
    /// </summary>
    public async Task<int> Challenge(string targetId, string? lang, int questionCount, List<string> categoryIds, List<int> levels)
    {
        if (RequirePlayer() is not { } challengerId) return (int)LiveChallengeResult.NotFound;
        if (challengerId == targetId) return (int)LiveChallengeResult.SelfChallenge;

        var me = await players.GetAsync(challengerId);
        if (me is null) return (int)LiveChallengeResult.NotFound;
        if (!me.Friends.Contains(targetId) && !await WerePastOpponentsAsync(challengerId, targetId))
            return (int)LiveChallengeResult.NotFound;

        var resolvedLang = string.IsNullOrWhiteSpace(lang) ? me.Lang : lang.ToLanguage();
        var lobby = grains.GetGrain<ILiveMatchGrain>(ids.NewId());
        var view = await lobby.CreateLobbyAsync(ids.NewMatchCode(), challengerId, (int)resolvedLang, questionCount, categoryIds, levels, capacity: 2);

        return await Matchmaking.ChallengeAsync(ids.NewId(), challengerId, targetId, view.Id);
    }

    public Task<LiveChallengeAcceptResult> Accept(string challengeId)
        => RequirePlayer() is { } me
            ? Matchmaking.AcceptAsync(challengeId, me)
            : Task.FromResult(new LiveChallengeAcceptResult((int)LiveChallengeResult.NotFound, null, null));

    public Task<int> Decline(string challengeId)
        => RequirePlayer() is { } me ? Matchmaking.DeclineAsync(challengeId, me) : Task.FromResult((int)LiveChallengeResult.NotFound);

    private string? RequirePlayer() => Context.User is null || Context.User.IsGuest() ? null : Context.User.PlayerId();

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> both appear among the participants of
    /// some duel in <paramref name="a"/>'s own recent history (<see cref="CoParticipantLookback"/> most
    /// recent, either kind) — "demonstrably just played together", the one narrow relaxation of
    /// <see cref="Challenge"/>'s friendship gate. Checks <see cref="ArchivedMatch.Results"/> first,
    /// which already lists every real participant of a duel regardless of how many it held, and falls
    /// back to the legacy <c>ChallengerId</c>/<c>OpponentId</c> pair only for a row old enough to
    /// predate <see cref="ParticipantResult"/> — the same tolerate-both-shapes discipline the
    /// persistence layer already applies everywhere else a row this old can surface.
    /// </summary>
    private async Task<bool> WerePastOpponentsAsync(string a, string b)
    {
        var recent = await archive.ForPlayerAsync(a, CoParticipantLookback);
        return recent.Any(m => m.Results.Any(r => r.PlayerId == b) || m.ChallengerId == b || m.OpponentId == b);
    }

    private static LiveChallengeNotice ToNotice(LiveChallengeView c)
        => new(c.ChallengeId, c.ChallengerId, c.LobbyId, c.LobbyCode, c.ExpiresAt);
}
