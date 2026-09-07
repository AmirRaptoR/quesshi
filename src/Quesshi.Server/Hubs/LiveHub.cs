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
    /// No return value on purpose: right/wrong is withheld until <c>RoundRevealed</c> reaches both
    /// players together, and the grain's own answer already returns nothing but success either way.
    /// </summary>
    public async Task Answer(string matchId, int round, int choiceIndex)
    {
        var meId = Context.User!.PlayerId();
        if (meId is null) return;
        await grains.GetGrain<ILiveMatchGrain>(matchId).AnswerAsync(meId, round, choiceIndex);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (notifier is SignalRLiveNotifier tracker) await tracker.NoteDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
