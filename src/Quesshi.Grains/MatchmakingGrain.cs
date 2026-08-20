using Microsoft.Extensions.Logging;
using Orleans;
using Quesshi.Grains.Abstractions;
using Orleans.Runtime;
using Quesshi.Application.Ports;

namespace Quesshi.Grains;

/// <summary>
/// A single queue of players waiting for a stranger. One grain means no locking and no race:
/// two players hitting "random opponent" at the same instant are handled one after the other.
/// </summary>
public sealed class MatchmakingGrain(
    [PersistentState("matchmaking", "hot")] IPersistentState<MatchmakingState> state,
    IClock clock) : Grain, IMatchmakingGrain
{
    private static readonly TimeSpan Stale = TimeSpan.FromHours(48);

    public async Task<string?> FindOrQueueAsync(string playerId, int lang, string myMatchId)
    {
        Prune();

        var waiting = state.State.Waiting.FirstOrDefault(w => w.Lang == lang && w.PlayerId != playerId);
        if (waiting is not null)
        {
            state.State.Waiting.Remove(waiting);
            await state.WriteStateAsync();
            return waiting.MatchId;
        }

        state.State.Waiting.RemoveAll(w => w.PlayerId == playerId);
        state.State.Waiting.Add(new QueueEntry(playerId, lang, myMatchId, clock.Now));
        await state.WriteStateAsync();
        return null;
    }

    public async Task LeaveAsync(string playerId)
    {
        if (state.State.Waiting.RemoveAll(w => w.PlayerId == playerId) > 0)
            await state.WriteStateAsync();
    }

    public Task<int> WaitingCountAsync()
    {
        Prune();
        return Task.FromResult(state.State.Waiting.Count);
    }

    private void Prune()
    {
        // A queued match that has already forfeited is not worth handing to anybody.
        state.State.Waiting.RemoveAll(w => clock.Now - w.QueuedAt > Stale);
    }
}
