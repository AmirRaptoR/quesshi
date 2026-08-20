namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IMatchmakingGrain")]
public interface IMatchmakingGrain : IGrainWithIntegerKey
{
    /// <summary>Returns an opponent's open match to join, or null after queueing this player's own match.</summary>
    [Alias("FindOrQueueAsync")]
    Task<string?> FindOrQueueAsync(string playerId, int lang, string myMatchId);

    [Alias("LeaveAsync")]
    Task LeaveAsync(string playerId);
    [Alias("WaitingCountAsync")]
    Task<int> WaitingCountAsync();
}
