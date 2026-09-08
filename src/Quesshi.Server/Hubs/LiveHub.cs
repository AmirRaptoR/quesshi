using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Hubs;

/// <summary>
/// The live-duel transport #11 shipped everything for except this class — its own commit message
/// records it as blocked on #10. One hub for the duel's whole life: <c>Join</c> is the catch-up
/// read <see cref="Quesshi.Server.Api.LiveEndpoints.GetAsync"/> also serves, plus adding this
/// connection to the match's group; <c>Answer</c> forwards to the grain and trusts its silence —
/// a rejected answer gets no error, the next <c>RoundRevealed</c> push is authoritative;
/// <c>Leave</c> only drops the group, it never touches the duel itself (that is
/// <c>DELETE /api/live/{id}</c>, #12). Presence (<see cref="SignalRLiveNotifier.OpponentPresenceChangedAsync"/>)
/// is driven from <see cref="OnDisconnectedAsync"/> here, and from <see cref="Join"/> for "back" —
/// the grain never sees a connection, only <see cref="ILiveMatchGrain.AnswerAsync"/>/<c>JoinAsync</c> calls.
/// </summary>
[Authorize]
public sealed class LiveHub(
    IGrainFactory grains, IPlayerRepository players, IQuestionRepository questions,
    ICategoryRepository categories, IClock clock, ILiveNotifier notifier) : Hub
{
    internal static string GroupName(string matchId) => $"live:{matchId}";

    public async Task<LiveViewDto> Join(string matchId)
    {
        var meId = Context.User!.PlayerId() ?? throw new HubException("unauthenticated");
        var view = await grains.GetGrain<ILiveMatchGrain>(matchId).GetAsync(meId);
        if (view is null) throw new HubException("not_a_participant");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(matchId));

        if (notifier is SignalRLiveNotifier tracker)
            await tracker.NoteConnectedAsync(Context.ConnectionId, matchId, meId);

        var lookup = await players.LiveLookupAsync(view);
        return await view.ToLiveDtoAsync(clock.Now, questions, categories, lookup);
    }

    public Task Leave(string matchId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(matchId));

    /// <summary>
    /// The async-lobby twin of <see cref="Join"/> — issue #53's one bit of new plumbing. An async duel
    /// has no gameplay transport of its own (<c>ServeNext</c>/<c>Answer</c> stay plain REST: a run
    /// advances on the player's own schedule, not a shared clock), but its lobby phase wants the same
    /// live roster/settings push a live lobby gets, and <see cref="ILiveNotifier.LobbyUpdatedAsync"/>
    /// already reaches this hub's per-match group regardless of which grain fired it. Rather than
    /// stand up a second hub — one that would have to re-earn guest access <c>LobbyHub</c> deliberately
    /// refuses — this just adds the connection to the same group. It returns nothing: unlike
    /// <see cref="Join"/>'s <see cref="LiveViewDto"/>, there is no matching async view type this hub
    /// could build without pulling in the whole of <c>Mappers.ToSummary</c>'s dependencies for a page
    /// that already has to call <c>GET /api/matches/{id}</c> over REST anyway.
    /// </summary>
    public async Task JoinAsyncLobby(string matchId)
    {
        var meId = Context.User!.PlayerId() ?? throw new HubException("unauthenticated");
        var view = await grains.GetGrain<IMatchGrain>(matchId).GetAsync(meId);
        if (view is null) throw new HubException("not_a_participant");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(matchId));
    }

    public Task LeaveAsyncLobby(string matchId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(matchId));

    /// <summary>
    /// No return value on purpose: right/wrong is withheld until <c>RoundRevealed</c> reaches both
    /// players together, and the grain's own answer already returns nothing but success either way.
    /// </summary>
    public async Task Answer(string matchId, int round, int choiceIndex)
    {
        var meId = Context.User!.PlayerId();
        if (meId is null) return;
        await grains.GetGrain<ILiveMatchGrain>(matchId).AnswerAsync(meId, round, choiceIndex);
    }

    /// <summary>
    /// Guest-blocked regardless of what the client offered, the way <c>LobbyHub.QueueRandom</c>
    /// refuses a guest's press: the caller's own claim is checked first, then — since nothing about
    /// the *opponent*'s guest status crosses the wire until <see cref="ILiveMatchGrain.GetAsync"/>'s
    /// view names them — one repository read for the other side. Both checks run before the grain
    /// ever sees the press, so a guest calling this directly gets refused with no readiness recorded.
    /// </summary>
    public async Task<RematchOutcomeDto> Rematch(string matchId)
    {
        var meId = Context.User!.PlayerId() ?? throw new HubException("unauthenticated");
        if (Context.User!.IsGuest()) return new RematchOutcomeDto("refused");

        var grain = grains.GetGrain<ILiveMatchGrain>(matchId);
        var view = await grain.GetAsync(meId);
        if (view is null) return new RematchOutcomeDto("refused");

        // Rematch is still a two-player press: "the other seat", found the same way for a bigger
        // lobby too (the first participant that is not the caller) even though nothing here builds
        // the N-player rematch flow itself — that is issue #53's job, not this one's.
        var opponentId = view.Participants.FirstOrDefault(id => id != meId);
        if (opponentId is null) return new RematchOutcomeDto("refused"); // a lobby nobody joined has nobody to rematch with

        var opponent = await players.GetAsync(opponentId);
        if (opponent is null || opponent.IsGuest) return new RematchOutcomeDto("refused");

        var outcome = await grain.RequestRematchAsync(meId);
        return outcome.ToDto();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (notifier is SignalRLiveNotifier tracker) await tracker.NoteDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
